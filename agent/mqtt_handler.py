"""
MQTT message handling for communication with Unity/RTD.
"""
import json
import random
import ssl
import time
import paho.mqtt.client as mqtt_client

from .utils import trim_schema_data
from .context import update_dataframe_from_layer, get_current_config, set_image_data, set_chart_metadata_index
from .graph import graph
from .orchestrator import process_user_request
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
        set_chart_metadata_index(data["chart_metadata_index"])
        graph.update_state(
            get_current_config(),
            {"chart_metadata_index": data["chart_metadata_index"]},
        )
        print("Chart metadata index registered")
        return

    # Layer data update -- builds DataFrame and pushes metadata into graph state
    if data.get("message_type") == "layer_data_update":
        update_dataframe_from_layer(data)  # also calls graph.update_state internally
        return

    # Full chart details published on-demand by Unity (image + schema for a specific chart)
    if data.get("message_type") == "chart_details":
        image_data = data.get("image_data")
        image_format = data.get("image_format") or "png"
        if image_data:
            set_image_data(image_data, image_format)
            graph.update_state(get_current_config(), {
                "image_data": image_data,
                "image_format": image_format,
            })
            print(f"Chart details registered: image={bool(image_data)}, chart_id={data.get('chart_id')}")
        return

    # RTD data (chart metadata + optional screenshot from renderer)
    if "rtd_data_for_agent" in data:
        rtd_data = data["rtd_data_for_agent"]
        patch = {
            "chart_type":  rtd_data.get("chart_type"),
            "data_name":   rtd_data.get("data_name"),
        }
        # Only include image fields if actually present -- a None value would overwrite a valid
        # image that arrived earlier (e.g. from a prior rtd_data_for_agent with an image).
        image_data = rtd_data.get("image_data")
        image_format = rtd_data.get("image_format")
        if image_data:
            set_image_data(image_data, image_format or "png")
            patch["image_data"] = image_data
            patch["image_format"] = image_format or "png"

        schema = rtd_data.get("schema") or {}
        encoding = schema.get("encoding") or {}
        patch["color_field"] = (encoding.get("color") or {}).get("field") or None
        patch["vega_lite_schema"] = trim_schema_data(schema)
        overview = schema.get("overview")
        if overview:
            patch["chart_overview"] = overview

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
            )
        except Exception as e:
            print(f"Error processing request: {e}")
            import traceback
            traceback.print_exc()
            publish_message(
                response_text="I encountered an error processing your request.",
                followup_stage=False,
            )


def publish_message(
    response_text: str,
    rtd_command: dict = None,
    nodes: dict = None,
    followup_stage: bool = False,
    referents: dict = None
):
    """Publish agent response back to Unity over MQTT."""
    global _mqtt_client

    if _mqtt_client is None:
        print("MQTT client not connected")
        return

    payload = {
        "agent_response_for_user": {
            "response_text": response_text,
            "followup_stage": followup_stage
        }
    }

    if nodes is not None:
        payload["agent_response_for_user"]["nodes"] = nodes
    if rtd_command:
        payload["agent_response_for_user"]["rtd_command"] = rtd_command
    if referents:
        payload["agent_response_for_user"]["referents"] = referents

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


def create_mqtt_client() -> mqtt_client.Client:
    global _mqtt_client

    client_id = f'python-agent-{random.randint(0, 1000)}'
    client = mqtt_client.Client(
        client_id=client_id,
        callback_api_version=mqtt_client.CallbackAPIVersion.VERSION2
    )

    client.username_pw_set(MQTT_USERNAME, MQTT_PASSWORD)
    client.tls_set(tls_version=ssl.PROTOCOL_TLS)
    client.reconnect_delay_set(min_delay=1, max_delay=60)

    client.on_connect = on_connect
    client.on_message = on_message
    client.on_disconnect = on_disconnect

    _mqtt_client = client
    return client


def run():
    """Start the MQTT client loop."""
    client = create_mqtt_client()

    retry_delay = 1
    while True:
        try:
            print(f"Connecting to {MQTT_HOST}:{MQTT_PORT}...")
            client.connect(MQTT_HOST, MQTT_PORT)
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
