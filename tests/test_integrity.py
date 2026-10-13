"""Run with: python -m unittest discover -s tests -v"""
import hashlib
import json
import os
import random
import tempfile
import unittest
from pathlib import Path

from app.archive import restore
from app.codecs import METHODS, catalog, encode, decode, normalize_step
from app.storage import Store, now
from app.worker import execute


class CodecIntegrity(unittest.TestCase):
    def test_every_method_on_binary_text_runs_and_empty_input(self):
        samples = [b"", b"A", b"A"*4097, bytes(range(256))*13,
                   ("Точное восстановление данных. "*100).encode(),
                   random.Random(42).randbytes(12000)]
        available = {m["id"] for m in catalog() if m["available"]}
        self.assertEqual(available,{m["id"] for m in METHODS},"Install all requirements before the full check")
        for method in METHODS:
            step = normalize_step(dict(method=method["id"]))
            # Force frequent dictionary resets on the reference dictionary codecs.
            if method["id"] in ("lz78","lzw"):
                step["params"]["dictionary_size"] = 512
            for sample in samples:
                with self.subTest(method=method["id"],size=len(sample)):
                    self.assertEqual(decode(encode(sample,step),step),sample)

    def test_reference_dictionary_reset_boundaries(self):
        for seed in range(8):
            data = random.Random(seed).randbytes(70000)
            for method in ("lzw","lz78"):
                for limit in (512,1024,65536):
                    with self.subTest(method=method,seed=seed,limit=limit):
                        step = normalize_step(dict(method=method,params=dict(dictionary_size=limit)))
                        self.assertEqual(decode(encode(data,step),step),data)

    def test_parameter_validation(self):
        for bad in [dict(method="missing"),dict(method="gzip",params={"level":10}),
                    dict(method="zstd",params={"level":True}),dict(method="rle",params={"wrong":3})]:
            with self.assertRaises(ValueError):
                normalize_step(bad)
        self.assertEqual(normalize_step(dict(method="ppmp"))["method"],"ppmd")


class StoreAndArchive(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = Store(self.temp.name)
        self.raw = (b"\0"*200+b"dataset for compression benchmark\n"*50)*100
        self.file = self.store.add_bytes("../../source.bin",self.raw)

    def tearDown(self):
        self.temp.cleanup()

    def make_experiment(self,steps,priority=0,start_at=None):
        return self.store.create_experiment("Check",[self.file["id"]],
            [dict(label="Chain",steps=[normalize_step(dict(method=m)) for m in steps])],
            dict(priority=priority,start_at=start_at or now(),repeats=2,warmups=1,block_kib=64,timeout_seconds=60))

    def test_streamed_chain_archive_restore_and_metrics(self):
        self.make_experiment(["rle","huffman","zstd"])
        task_id = self.store.claim_next()
        execute(self.store,self.store.get_task(task_id))
        task = self.store.get_task(task_id)
        self.assertEqual(task["status"],"completed")
        result = task["result"]
        self.assertTrue(result["verified"])
        self.assertEqual(len(result["encode_samples_ms"]),2)
        self.assertEqual(result["input_bytes"],len(self.raw))
        self.assertEqual(result["output_bytes"],result["payload_bytes"]+result["overhead_bytes"])
        dest = Path(self.temp.name)/"restored.bin"
        restore(self.store.root/"artifacts"/result["artifact"],dest)
        self.assertEqual(dest.read_bytes(),self.raw)

    def test_empty_file_archive(self):
        empty = self.store.add_bytes("empty.bin",b"")
        self.store.create_experiment("Empty",[empty["id"]],
            [dict(label="LZFSE",steps=[normalize_step(dict(method="lzfse"))])],
            dict(priority=0,start_at=now(),repeats=1,warmups=0,block_kib=64,timeout_seconds=60))
        task_id = self.store.claim_next()
        execute(self.store,self.store.get_task(task_id))
        task = self.store.get_task(task_id)
        self.assertIsNone(task["result"]["reduction_pct"])
        dest = Path(self.temp.name)/"empty-restored"
        restore(self.store.root/"artifacts"/task["result"]["artifact"],dest)
        self.assertEqual(dest.read_bytes(),b"")

    def test_priority_schedule_cancel_retry_and_restart(self):
        low = self.make_experiment(["gzip"],priority=0)
        high = self.make_experiment(["zstd"],priority=10)
        future = self.make_experiment(["lzma2"],priority=50,start_at="2099-01-01T00:00:00+00:00")
        claimed = self.store.claim_next()
        self.assertEqual(self.store.get_task(claimed)["experiment_id"],high)
        reopened = Store(self.temp.name)
        reopened.recover()
        self.assertEqual(reopened.get_task(claimed)["status"],"queued")
        reopened.cancel(claimed)
        self.assertTrue(reopened.retry(claimed))
        reopened.cancel(claimed)
        next_id = reopened.claim_next()
        self.assertEqual(reopened.get_task(next_id)["experiment_id"],low)
        reopened.cancel(next_id)
        self.assertIsNone(reopened.claim_next(),"Future task must remain queued")

    def test_content_addressing_and_filename_boundary(self):
        duplicate = self.store.add_bytes("other.bin",self.raw)
        self.assertEqual(self.file["name"],"source.bin")
        self.assertEqual(self.store.blob_path(self.file),self.store.blob_path(duplicate))
        self.store.delete_file(self.file["id"])
        self.assertTrue(self.store.blob_path(duplicate).exists())
        self.store.delete_file(duplicate["id"])
        self.assertFalse(self.store.blob_path(duplicate).exists())


if __name__ == "__main__":
    unittest.main()
