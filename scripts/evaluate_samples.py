"""Developer-only evaluator. Never uploads pictures or reads API keys."""
import argparse
import json
from pathlib import Path

TYPES = ["Unknown", "Vehicle", "Battery", "Charger", "Accessory"]
ACCURACY_FIELDS = {"name", "spec", "color", "materialCode", "marker"}
CRITICAL_FIELDS = {"originalOrder", "type", "rawQuantity", "rawUnit"}


def lower_keys(obj):
    return {key.lower(): value for key, value in obj.items()}


def evaluate(baseline, result):
    result = lower_keys(result)
    result = lower_keys(result.get("recognition") or result)
    rows = [lower_keys(row) for row in result.get("rows", [])]
    differences, checked, correct, unknown = [], 0, 0, 0
    for index, expected in enumerate(baseline["rows"]):
        actual = rows[index] if index < len(rows) else {}
        for field, value in expected.items():
            if value is None:
                unknown += 1
                continue
            actual_value = actual.get(field.lower())
            if field == "type" and isinstance(actual_value, int):
                actual_value = TYPES[actual_value] if 0 <= actual_value < len(TYPES) else "Invalid"
            checked += field in ACCURACY_FIELDS
            if actual_value == value:
                correct += field in ACCURACY_FIELDS
            else:
                differences.append({"row": index + 1, "field": field, "expected": value, "actual": actual_value})
    totals = {kind: 0 for kind in baseline["sectionTotals"]}
    for row in rows:
        kind = row.get("type")
        if isinstance(kind, int) and 0 <= kind < len(TYPES):
            kind = TYPES[kind]
        quantity = row.get("rawquantity", "")
        if kind in totals and isinstance(quantity, str) and quantity.isascii() and quantity.isdigit():
            totals[kind] += int(quantity)
    return {
        "sample": baseline["id"], "expectedRows": baseline["rowCount"], "actualRows": len(rows),
        "fieldAccuracy": correct / checked if checked else None, "checkedFields": checked,
        "pendingHumanFields": unknown, "differences": differences, "actualTotals": totals,
        "totalsMatch": totals == baseline["sectionTotals"],
        "criticalFieldsAllCorrect": len(rows) == baseline["rowCount"] and not any(diff["field"] in CRITICAL_FIELDS for diff in differences),
        "elapsed": result.get("elapsed"), "inputTokens": result.get("inputtokens"), "outputTokens": result.get("outputtokens"),
        "acceptance": "Requires real-call evidence and completion of uncertain mandatory baseline fields"
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", default="docs/样单人工基准.json")
    parser.add_argument("--sample", required=True, choices=["sample-1", "sample-2"])
    parser.add_argument("--result", required=True, help="Native model rows or locally saved attachment metadata JSON")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    samples = json.loads(Path(args.baseline).read_text(encoding="utf-8"))["samples"]
    baseline = next(sample for sample in samples if sample["id"] == args.sample)
    result = json.loads(Path(args.result).read_text(encoding="utf-8"))
    report = evaluate(baseline, result)
    Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "differences"}, ensure_ascii=False))


if __name__ == "__main__":
    main()
