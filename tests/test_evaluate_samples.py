import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("evaluate_samples", Path(__file__).resolve().parents[1] / "scripts/evaluate_samples.py")
evaluator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluator)


class EvaluatorTests(unittest.TestCase):
    def setUp(self):
        self.baseline = {"id": "test", "rowCount": 1, "sectionTotals": {"Vehicle": 2},
                         "rows": [{"originalOrder": "1", "type": "Vehicle", "rawQuantity": "2", "name": None}]}
        self.result = {"actualQuantityColumn": True, "rows": [{"originalOrder": "1", "type": "Vehicle", "rawQuantity": "2"}],
                       "sectionTotals": {"Vehicle": 99}}

    def test_returned_totals_are_distinct_from_calculated_totals(self):
        report = evaluator.evaluate(self.baseline, self.result)
        self.assertTrue(report["totalsMatch"])
        self.assertFalse(report["returnedTotalsMatch"])
        self.assertEqual(1, report["pendingHumanFields"])

    def test_invalid_quantity_cannot_be_silently_excluded_from_total(self):
        self.result["rows"][0]["rawQuantity"] = "invalid"
        report = evaluator.evaluate(self.baseline, self.result)
        self.assertIsNone(report["actualTotals"]["Vehicle"])
        self.assertFalse(report["criticalFieldsAllCorrect"])

    def test_missing_actual_column_cannot_pass_critical_checks(self):
        del self.result["actualQuantityColumn"]
        self.assertFalse(evaluator.evaluate(self.baseline, self.result)["criticalFieldsAllCorrect"])

    def test_boolean_type_is_not_treated_as_numeric_vehicle_enum(self):
        self.result["rows"][0]["type"] = True
        report = evaluator.evaluate(self.baseline, self.result)
        self.assertFalse(report["criticalFieldsAllCorrect"])
        self.assertIsNone(report["actualTotals"]["Vehicle"])

    def test_exported_replay_record_remains_explicitly_not_real_evidence(self):
        record = {"recognition": self.result, "realCallEvidence": False}
        self.assertIs(evaluator.evaluate(self.baseline, record)["realCallEvidence"], False)


if __name__ == "__main__":
    unittest.main()
