import json
from pathlib import Path
import uuid
import unittest
import worker
from PIL import Image


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        test_root = Path(__file__).resolve().parents[1] / 'artifacts/ocr-protocol'
        test_root.mkdir(parents=True, exist_ok=True)
        # Inherit workspace ACLs. Windows sandbox tokens cannot access the
        # owner-only directories created by TemporaryDirectory(mode=0700).
        self.root = test_root / ('stock-ocr-protocol-' + uuid.uuid4().hex)
        self.root.mkdir()
        self.photo = self.root / '照片.png'
        Image.new('RGB', (32, 32), 'white').save(self.photo)
        self.original_root = worker.MODEL_ROOT
        self.original_engine = worker._engine
        worker._engine = None

    def tearDown(self):
        worker.MODEL_ROOT = self.original_root
        worker._engine = self.original_engine
        # Retain test-owned artifacts for inspection and normal workspace cleanup.

    def request(self):
        return {'protocolVersion': 1, 'requestId': 'test', 'imagePath': str(self.photo)}

    def test_missing_models(self):
        worker.MODEL_ROOT = self.root
        with self.assertRaises(worker.WorkerError) as error:
            worker.process(self.request())
        self.assertEqual(error.exception.code, 'MODEL_MISSING')

    def test_invalid_model_checksum(self):
        worker.MODEL_ROOT = self.root
        (self.root / 'model.onnx').write_bytes(b'invalid model')
        (self.root / 'manifest.json').write_text(json.dumps({'sha256': {'model.onnx': '0' * 64}}))
        with self.assertRaises(worker.WorkerError) as error:
            worker.process(self.request())
        self.assertEqual(error.exception.code, 'MODEL_INVALID')

    def test_invalid_image_content(self):
        self.photo.write_bytes(b'invalid image')
        with self.assertRaises(worker.WorkerError) as error:
            worker.process(self.request())
        self.assertEqual(error.exception.code, 'IMAGE_INVALID')


if __name__ == '__main__':
    unittest.main()
