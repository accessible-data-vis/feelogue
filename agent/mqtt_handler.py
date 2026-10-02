"""
MQTT message handling for communication with Unity/RTD.
"""
import json
import random
import ssl
import hashlib
import threading
import time
import uuid
import paho.mqtt.client as mqtt_client

from .utils import trim_schema_data
from .postprocessing import split_into_chunks
from .layer_overview import compute_facts, generate_layer_overview, template_layer_overview
from .context import (update_dataframe_from_layer, get_current_config, reset_context_keep_messages,
                      get_generated_overview, set_generated_overview, held_stage, held_generation,
                      advance_held, take_held, describe_pieces, set_chart_spec)
from .graph import graph
from .orchestrator import process_user_request, process_held_request
from .config import (
    MQTT_HOST,
    MQTT_PORT,
    MQTT_USERNAME,
    MQTT_PASSWORD,
    MQTT_TOPIC_IN,
    MQTT_TOPIC_OUT,
)

# Global client reference (set after connection)
_mqtt_client = None


def on_message(client, userdata, msg):
    """Handle inbound MQTT messages."""
    payload = msg.payload.decode('utf-8', errors='ignore').strip()
    print(f"\nReceived: {payload[:200]}...")

    if payload.strip().lower() in ('exit.', 'stop.', 'quit.'):
        print("Exiting...")
        client.disconnect()
        return

    try:
        data = json.loads(payload)
    except json.JSONDecodeError:
        print("Invalid JSON format in message.")
        return

    # Chart metadata index (boot message from Unity)
    if "chart_metadata_index" in data:
        graph.update_state(
            get_current_config(),
            {"chart_metadata_index": data["chart_metadata_index"]},
        )
        print("Chart metadata index registered")
        return

    # Layer data update: builds the DataFrame and pushes metadata into graph state
    if data.get("message_type") == "layer_data_update":
        update_dataframe_from_layer(data)  # also calls graph.update_state internally
        # First data after a load the agent asked for: run the held rest of that request.
        if held_stage() == "awaiting_data":
            _run_held_pieces(take_held())
        return

    # Full chart details published on-demand by Unity (image + schema for a specific chart)
    if data.get("message_type") == "chart_details":
        image_data = data.get("image_data")
        image_format = data.get("image_format") or "png"
        if image_data:
            graph.update_state(get_current_config(), {
                "image_data": image_data,
                "image_format": image_format,
            })
            print(f"Chart details registered: image={bool(image_data)}, chart_id={data.get('chart_id')}")
        return

    # RTD data (chart metadata + optional screenshot from renderer)
    if "rtd_data_for_agent" in data:
        rtd_data = data["rtd_data_for_agent"]
        reset_context_keep_messages()
        advance_held("awaiting_load", "awaiting_data")   # its data follows this message
        patch = {
            "chart_type":  rtd_data.get("chart_type"),
            "data_name":   rtd_data.get("data_name"),
            "display_marks": (rtd_data.get("rendered") or {}).get("series"),
        }
        # Only include image fields if present; otherwise they stay cleared by the
        # reset above until a chart_details message for this chart supplies one.
        image_data = rtd_data.get("image_data")
        image_format = rtd_data.get("image_format")
        if image_data:
            patch["image_data"] = image_data
            patch["image_format"] = image_format or "png"

        schema = rtd_data.get("schema") or {}
        set_chart_spec(schema or None)
        encoding = schema.get("encoding") or {}
        patch["color_field"] = (encoding.get("color") or {}).get("field") or None
        trimmed = trim_schema_data(schema)
        schema_str = json.dumps(trimmed) if trimmed else None
        patch["vega_lite_schema"] = schema_str
        overview = schema.get("overview")
        if overview:
            patch["chart_overview"] = overview
        else:
            # No authored layer text: generate it off the MQTT thread, since an LLM
            # call here would stall every other message.
            threading.Thread(
                target=_publish_generated_overview,
                args=(schema, rtd_data.get("rendered"), rtd_data.get("data_name"), rtd_data.get("chart_type")),
                daemon=True,
            ).start()

        graph.update_state(get_current_config(), patch)
        print(f"RTD data registered: chart_type={rtd_data.get('chart_type')}, data_name={rtd_data.get('data_name')}, image={bool(image_data)}")
        return

    # User request
    if "user_request_for_agent" in data:
        try:
            result = process_user_request(payload)
            publish_message(
                response_text=result.get("response", ""),
                rtd_command=result.get("rtd_command"),
                nodes=result.get("nodes"),
                followup_stage=result.get("followup_stage", False),
                referents=result.get("referents"),
                chunks=result.get("chunks"),
                presentation=result.get("presentation"),
            )
            if held_stage() == "awaiting_load":
                _expire_held_later(held_generation())
        except Exception as e:
            print(f"Error processing request: {e}")
            import traceback
            traceback.print_exc()
            publish_message(
                response_text="I encountered an error processing your request.",
                followup_stage=False,
            )


HELD_EXPIRY_S = 20   # a load normally confirms within a second or two


def _run_held_pieces(pieces: list[dict]):
    """Run the held rest of a request against the chart that just loaded."""
    if not pieces:
        return
    try:
        result = process_held_request(pieces)
        publish_message(
            response_text=result.get("response", ""),
            rtd_command=result.get("rtd_command"),
            nodes=result.get("nodes"),
            followup_stage=result.get("followup_stage", False),
            referents=result.get("referents"),
            chunks=result.get("chunks"),
            presentation=result.get("presentation"),
        )
    except Exception as e:
        print(f"Error running held request: {e}")
        import traceback
        traceback.print_exc()
        publish_message(response_text=f"I loaded the chart, but couldn't do the rest: {describe_pieces(pieces)}.")


def _expire_held_later(generation: int):
    """If the chart never arrives, drop the held pieces and say what didn't run."""
    def expire():
        if held_stage() in ("awaiting_load", "awaiting_data"):
            pieces = take_held(generation)
            if pieces:
                publish_message(response_text=(
                    f"I didn't do the rest of your request, {describe_pieces(pieces)}, "
                    "since the chart didn't load."))
    timer = threading.Timer(HELD_EXPIRY_S, expire)
    timer.daemon = True
    timer.start()


def _publish_generated_overview(schema: dict, rendered: dict | None, data_name: str | None,
                                chart_type: str | None = None):
    """Generate presentation text for a chart without an authored overview and send
    it to Unity. Always answers, since Unity waits for it: fixed sentences if the model
    fails (not cached, so the next load retries), or a null overview when there is
    nothing to describe."""
    if not data_name or _mqtt_client is None:
        return
    chart_type = chart_type or (rendered or {}).get("chart_type")
    overview = None
    try:
        values = (schema.get("data") or {}).get("values") or []
        digest = hashlib.sha1(json.dumps([values, rendered], sort_keys=True, default=str).encode()).hexdigest()
        overview = get_generated_overview(data_name, digest)
        if overview is None:
            facts = compute_facts(schema, rendered)
            if not facts:
                print(f"[layer_overview] nothing to describe for '{data_name}'")
            else:
                try:
                    overview = generate_layer_overview(facts)
                except Exception as e:
                    print(f"[layer_overview] phrasing failed for '{data_name}': {e}")
                    overview = None
                if overview:
                    set_generated_overview(data_name, digest, overview)
                else:
                    overview = template_layer_overview(facts)
                    print(f"[layer_overview] using fixed sentences for '{data_name}'")
    except Exception as e:
        print(f"[layer_overview] generation failed for '{data_name}': {e}")
        overview = None
    payload = {"chart_overview_for_rtd": {"data_name": data_name, "chart_type": chart_type, "overview": overview}}
    _mqtt_client.publish(MQTT_TOPIC_OUT, json.dumps(payload), qos=1, retain=False)
    print(f"[layer_overview] sent layer text for '{data_name}' "
          f"({len(overview) if overview else 'none'} layers)")


def publish_message(
    response_text: str,
    rtd_command: dict = None,
    nodes: dict = None,
    followup_stage: bool = False,
    referents: dict = None,
    chunks: list = None,
    presentation: str = None,
):
    """Publish a reply to Unity. Always includes `chunks`: Unity plays them verbatim
    (it doesn't split text), and highlight nodes reference them by index."""
    global _mqtt_client

    if _mqtt_client is None:
        print("MQTT client not connected")
        return

    payload = {
        "agent_response_for_user": {
            "response_text": response_text,
            "chunks": chunks if chunks else split_into_chunks(response_text),
            "followup_stage": followup_stage,
            # Unique per publish: two arrivals with the same id were duplicated in
            # delivery, different ids mean the agent published twice.
            "message_id": uuid.uuid4().hex,
        }
    }

    if nodes is not None:
        payload["agent_response_for_user"]["nodes"] = nodes
    if rtd_command:
        payload["agent_response_for_user"]["rtd_command"] = rtd_command
    if referents:
        payload["agent_response_for_user"]["referents"] = referents
    if presentation:
        # "start": begin the presentation after this reply. "skip": this reply loads
        # a chart and the rest of the request follows, so don't start it on load.
        payload["agent_response_for_user"]["presentation"] = presentation

    response_json = json.dumps(payload)
    info = _mqtt_client.publish(MQTT_TOPIC_OUT, response_json, qos=1, retain=False)
    status = getattr(info, "rc", None)

    if status == mqtt_client.MQTT_ERR_SUCCESS:
        print(f"Sent response to '{MQTT_TOPIC_OUT}'")
    else:
        print(f"Error: Failed to send response. rc={status}")


def on_connect(client, userdata, flags, reason_code, properties):
    if reason_code == 0 or str(reason_code) == "Success":
        print("Connected to MQTT broker")
        client.subscribe(MQTT_TOPIC_IN)
        print(f"Subscribed to '{MQTT_TOPIC_IN}'")
    else:
        print(f"Error: Failed to connect. Reason: {reason_code}")


def on_disconnect(client, userdata, disconnect_flags, reason_code, properties):
    if reason_code == 0:
        print("Disconnected from MQTT broker cleanly.")
    else:
        print(f"Warning: Unexpected disconnect from MQTT broker (rc={reason_code}). Will attempt to reconnect...")


def create_mqtt_client(local: bool = False) -> mqtt_client.Client:
    """Client for the local broker (no TLS or credentials) or the remote one from .env."""
    global _mqtt_client

    client_id = f'python-agent-{random.randint(0, 1000)}'
    client = mqtt_client.Client(
        client_id=client_id,
        callback_api_version=mqtt_client.CallbackAPIVersion.VERSION2
    )

    # A local broker (mosquitto in local-only mode) is unencrypted and anonymous
    if not local:
        client.username_pw_set(MQTT_USERNAME, MQTT_PASSWORD)
        client.tls_set(tls_version=ssl.PROTOCOL_TLS)
    client.reconnect_delay_set(min_delay=1, max_delay=60)

    client.on_connect = on_connect
    client.on_message = on_message
    client.on_disconnect = on_disconnect

    _mqtt_client = client
    return client


def run(local: bool = True):
    """Start the MQTT client loop.

    local=True (the default, matching Unity's default) connects to
    localhost:1883 unencrypted with no credentials, so other agents on the
    shared broker can't cross-talk. local=False uses the remote broker from
    .env."""
    if not local:
        missing = [k for k, v in (("MQTT_REMOTE_HOST", MQTT_HOST),
                                  ("MQTT_REMOTE_USERNAME", MQTT_USERNAME),
                                  ("MQTT_REMOTE_PASSWORD", MQTT_PASSWORD)) if not v]
        if missing:
            raise ValueError(f"--remote needs {', '.join(missing)} in .env")
    host = "localhost" if local else MQTT_HOST
    port = 1883 if local else MQTT_PORT
    client = create_mqtt_client(local=local)

    retry_delay = 1
    while True:
        try:
            print(f"Connecting to {host}:{port}{' (local broker)' if local else ''}...")
            client.connect(host, port)
            break
        except Exception as e:
            print(f"Warning: Connection failed: {e}. Retrying in {retry_delay}s...")
            time.sleep(retry_delay)
            retry_delay = min(retry_delay * 2, 60)

    print("Starting message loop. Press Ctrl+C to exit.")
    try:
        client.loop_forever()
    except KeyboardInterrupt:
        print("\nShutting down...")
        client.disconnect()
