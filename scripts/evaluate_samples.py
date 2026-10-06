"""Developer-only evaluator. Never uploads pictures or reads API keys."""
import argparse
import json
from pathlib import Path

TYPES = ["Unknown", "Vehicle", "Battery", "Charger", "Accessory"]
ACCURACY_FIELDS = {"name", "spec", "color", "materialCode", "marker"}
CRITICAL_FIELDS = {"originalOrder", "type", "rawQuantity", "rawUnit", "materialCode", "marker"}


def lower_keys(obj):
    return {key.lower(): value for key, value in obj.items()}


def evaluate(baseline, result):
    record = lower_keys(result)
    diagnostic = lower_keys(record.get("diagnostic") or {})
    result = lower_keys(record.get("recognition") or record.get("result") or result)
    rows = [lower_keys(row) for row in result.get("rows", [])]
    differences, checked, correct, pending = [], 0, 0, []
    for index, expected in enumerate(baseline["rows"]):
        actual = rows[index] if index < len(rows) else {}
        for field, value in expected.items():
            if value is None:
                pending.append({"row": index + 1, "field": field})
                continue
            actual_value = actual.get(field.lower())
            if field == "type" and isinstance(actual_value, int) and not isinstance(actual_value, bool):
                actual_value = TYPES[actual_value] if 0 <= actual_value < len(TYPES) else "Invalid"
            checked += field in ACCURACY_FIELDS
            if actual_value == value:
                correct += field in ACCURACY_FIELDS
            else:
                differences.append({"row": index + 1, "field": field, "expected": value, "actual": actual_value})
    totals = {kind: 0 for kind in baseline["sectionTotals"]}
    for row in rows:
        kind = row.get("type")
        if isinstance(kind, int) and not isinstance(kind, bool) and 0 <= kind < len(TYPES):
            kind = TYPES[kind]
        quantity = row.get("rawquantity", "")
        if kind not in totals:
            # An unknown section cannot safely be omitted from a seemingly correct sum.
            totals = {kind: None for kind in totals}
        elif isinstance(quantity, str) and quantity.isascii() and quantity.isdigit() and int(quantity) <= 2147483647:
            if totals[kind] is not None:
                totals[kind] += int(quantity)
        else:
            totals[kind] = None
    returned = result.get("sectiontotals") or {}
    returned_totals = {}
    for kind in baseline["sectionTotals"]:
        value = returned.get(kind)
        if isinstance(value, str) and value.isascii() and value.isdigit() and int(value) <= 2147483647:
            value = int(value)
        if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= 2147483647:
            value = None
        returned_totals[kind] = value
    column = result.get("actualquantitycolumn")
    actual_column = column is True or isinstance(column, str) and column.strip().lower() == "true"
    model_differences, parsing_changes = [], []
    model_text = diagnostic.get("modeltext") or record.get("modeltext")
    if model_text:
        try:
            text = model_text.strip()
            if text.startswith("```") and text.endswith("```"):
                text = text.split("\n", 1)[1][:-3].strip()
            model_rows = [lower_keys(row) for row in json.loads(text).get("rows", [])]
            for index, expected in enumerate(baseline["rows"]):
                model_row = model_rows[index] if index < len(model_rows) else {}
                parsed_row = rows[index] if index < len(rows) else {}
                for field, value in expected.items():
                    raw = model_row.get(field.lower())
                    parsed = parsed_row.get(field.lower())
                    if field == "type" and isinstance(parsed, int) and not isinstance(parsed, bool) and 0 <= parsed < len(TYPES):
                        parsed = TYPES[parsed]
                    if raw != parsed:
                        parsing_changes.append({"row": index + 1, "field": field, "model": raw, "parsed": parsed})
                    if value is not None and raw != value:
                        model_differences.append({"row": index + 1, "field": field, "expected": value, "model": raw})
        except (ValueError, KeyError, IndexError, TypeError):
            pass
    return {
        "sample": baseline["id"], "expectedRows": baseline["rowCount"], "actualRows": len(rows),
        "fieldAccuracy": correct / checked if checked else None, "checkedFields": checked,
        "pendingHumanFields": len(pending), "pendingFields": pending, "differences": differences,
        "modelDifferences": model_differences, "parsingChanges": parsing_changes, "actualTotals": totals,
        "totalsMatch": totals == baseline["sectionTotals"],
        "returnedTotals": returned_totals, "returnedTotalsMatch": returned_totals == baseline["sectionTotals"],
        "actualQuantityColumnConfirmed": actual_column,
        "criticalFieldsAllCorrect": actual_column and len(rows) == baseline["rowCount"] and not any(diff["field"] in CRITICAL_FIELDS for diff in differences),
        "realCallEvidence": record.get("realcallevidence"),
        "elapsed": result.get("elapsed"), "inputTokens": result.get("inputtokens"), "outputTokens": result.get("outputtokens"),
        "acceptance": "Requires real-call evidence and completion of uncertain mandatory baseline fields"
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", default="docs/样单人工基准.json")
    parser.add_argument("--sample", required=True)
    parser.add_argument("--result", required=True, help="Native model rows or locally saved attachment metadata JSON")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    samples = json.loads(Path(args.baseline).read_text(encoding="utf-8"))["samples"]
    baseline = next((sample for sample in samples if sample["id"] == args.sample), None)
    if baseline is None:
        parser.error("Sample not found in baseline: " + args.sample)
    result = json.loads(Path(args.result).read_text(encoding="utf-8"))
    report = evaluate(baseline, result)
    Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "differences"}, ensure_ascii=False))


if __name__ == "__main__":
    main()
