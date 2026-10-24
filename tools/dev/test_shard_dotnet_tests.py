from __future__ import annotations

from collections import Counter
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("shard-dotnet-tests.py")


def shard_filters(cases: list[str], count: int, split_methods: bool = False) -> list[list[str]]:
    with tempfile.TemporaryDirectory() as directory:
        test_list = Path(directory) / "tests.txt"
        test_list.write_text(
            "The following Tests are available:\n" + "\n".join(cases) + "\n",
            encoding="utf-8",
        )
        assignments = []
        for index in range(count):
            command = [sys.executable, str(SCRIPT), str(test_list), str(index), str(count)]
            if split_methods:
                command.append("--split-methods")
            result = subprocess.run(command, check=True, capture_output=True, text=True)
            assignments.append(result.stdout.strip().split("|"))
        return assignments


class ShardDotnetTests(unittest.TestCase):
    def test_method_filters_cover_each_case_once_and_balance_theory_cases(self) -> None:
        cases = [
            "Example.Space.DroneTests.Theory(value: 1)",
            "Example.Space.DroneTests.Theory(value: 2)",
            "Example.Space.DroneTests.Theory(value: 3)",
            *(f"Example.Space.OtherTests.Test{index}" for index in range(10)),
        ]
        methods = Counter(name.split("(", 1)[0] for name in cases)
        assignments = shard_filters(cases, 4, split_methods=True)
        methods_by_shard = [
            [filter.removeprefix("FullyQualifiedName=") for filter in shard]
            for shard in assignments
        ]

        assigned = [method for shard in methods_by_shard for method in shard]
        self.assertCountEqual(assigned, methods)
        loads = [sum(methods[method] for method in shard) for shard in methods_by_shard]
        self.assertEqual(sorted(loads), [3, 3, 3, 4])
        self.assertEqual(assigned.count("Example.Space.DroneTests.Theory"), 1)

    def test_default_filters_keep_class_sharding(self) -> None:
        cases = [
            "Example.Space.DroneTests.First",
            "Example.Space.DroneTests.Second",
            "Example.Space.OtherTests.Only",
        ]
        assignments = shard_filters(cases, 2)
        flat = [filter for shard in assignments for filter in shard]

        self.assertCountEqual(
            flat,
            [
                "FullyQualifiedName~Example.Space.DroneTests.",
                "FullyQualifiedName~Example.Space.OtherTests.",
            ],
        )
        self.assertEqual(
            sum(filter == "FullyQualifiedName~Example.Space.DroneTests." for filter in assignments[0]),
            1,
        )


if __name__ == "__main__":
    unittest.main()
