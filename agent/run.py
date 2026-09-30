#!/usr/bin/env python3
"""
Entry point for running the RTD conversational agent.

    python -m agent.run             # local broker: localhost:1883, no TLS, no auth
    python -m agent.run --remote    # remote broker from .env (HiveMQ, TLS)
"""
import argparse

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="RTD conversational agent")
    parser.add_argument("--local", action="store_true",
                        help="connect to the local MQTT broker (the default; "
                             "kept for compatibility)")
    parser.add_argument("--remote", action="store_true",
                        help="connect to the remote broker from .env (HiveMQ, TLS) "
                             "instead of the local one (localhost:1883)")
    args = parser.parse_args()

    from agent.mqtt_handler import run
    run(local=not args.remote)
