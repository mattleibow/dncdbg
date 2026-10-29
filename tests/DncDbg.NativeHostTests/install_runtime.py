"""Compatibility entrypoint for the product runtime asset installer."""
import runpy
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/DncDbg.TargetHost"))
from runtime_assets import LOCK, install

if __name__ == "__main__":
    runpy.run_path(str(ROOT / "src/DncDbg.TargetHost/runtime_assets.py"), run_name="__main__")
