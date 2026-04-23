#!/usr/bin/env python3
"""
Generate PNG preview from Vega-Lite JSON specification.
Uses vl-convert library to render high-quality chart previews.

Usage:
    python3 generate_chart_preview.py --json-path <input.json> --png-path <output.png>

Example:
    python3 generate_chart_preview.py --json-path compiled-vl-bar-water-new.json --png-path chart-bar-water-new.png
"""

import argparse
import sys
import os

try:
    import vl_convert as vlc
except ImportError:
    print(" Error: vl-convert library not found!", file=sys.stderr)
    print("Install it with: pip install vl-convert-python", file=sys.stderr)
    sys.exit(1)


def generate_preview(json_path, png_path, scale_factor=2):
    """
    Generate PNG preview from Vega-Lite JSON specification.

    Args:
        json_path: Path to input Vega-Lite JSON file
        png_path: Path to output PNG file
        scale_factor: Scaling factor for PNG resolution (default: 2 for high-DPI)

    Returns:
        True if successful, False otherwise
    """
    try:
        # Validate input file exists
        if not os.path.exists(json_path):
            print(f" Error: JSON file not found: {json_path}", file=sys.stderr)
            return False

        print(f" Converting {json_path} to {png_path}...")

        # Read Vega-Lite JSON specification
        with open(json_path, "r", encoding="utf-8") as f:
            spec = f.read()

        # Convert Vega-Lite spec to PNG
        # scale_factor controls output resolution (2 = 2x native resolution)
        png_data = vlc.vegalite_to_png(spec, scale=scale_factor)

        # Ensure output directory exists
        output_dir = os.path.dirname(png_path)
        if output_dir and not os.path.exists(output_dir):
            os.makedirs(output_dir, exist_ok=True)

        # Save PNG output
        with open(png_path, "wb") as f:
            f.write(png_data)

        print(f" Successfully generated preview: {png_path}")
        return True

    except Exception as e:
        print(f" Error generating preview: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
        return False


def main():
    parser = argparse.ArgumentParser(
        description="Generate PNG preview from Vega-Lite JSON specification"
    )
    parser.add_argument(
        "--json-path",
        required=True,
        help="Path to input Vega-Lite JSON file"
    )
    parser.add_argument(
        "--png-path",
        required=True,
        help="Path to output PNG file"
    )
    parser.add_argument(
        "--scale",
        type=float,
        default=2.0,
        help="PNG scale factor for resolution (default: 2.0)"
    )

    args = parser.parse_args()

    success = generate_preview(args.json_path, args.png_path, args.scale)
    sys.exit(0 if success else 1)


if __name__ == "__main__":
    main()
