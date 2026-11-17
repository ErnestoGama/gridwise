"""Command line entry point: ``python3 -m gridwise_fixtures --out DIR``."""

from __future__ import annotations

import argparse
import sys
from datetime import date
from pathlib import Path

from .generator import (
    ALLOWED_INTERVALS,
    DEFAULT_DAYS,
    DEFAULT_INTERVAL_MINUTES,
    DEFAULT_REFERENCE_DATE,
    DEFAULT_SEED,
    Options,
    generate,
    write,
)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="gridwise_fixtures", description=__doc__)
    parser.add_argument("--out", type=Path, required=True, help="output directory")
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument(
        "--reference-date",
        type=date.fromisoformat,
        default=DEFAULT_REFERENCE_DATE,
        help="first day of the period, UTC (YYYY-MM-DD)",
    )
    parser.add_argument("--days", type=int, default=DEFAULT_DAYS)
    parser.add_argument(
        "--interval-minutes", type=int, default=DEFAULT_INTERVAL_MINUTES, choices=ALLOWED_INTERVALS
    )
    parser.add_argument("--no-edge-cases", action="store_true", help="leave out injected problems")
    args = parser.parse_args(argv)

    try:
        opts = Options(
            seed=args.seed,
            reference_date=args.reference_date,
            days=args.days,
            interval_minutes=args.interval_minutes,
            edge_cases=not args.no_edge_cases,
        )
    except ValueError as exc:
        parser.error(str(exc))

    fx = generate(opts)
    for path in write(fx, args.out):
        print(path)
    print(
        f"{len(fx.meters)} meters, {len(fx.readings)} readings, "
        f"{len(fx.edge_cases)} edge cases (seed {opts.seed})",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
