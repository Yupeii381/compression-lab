"""HTTP boundary checks; requires httpx (development only)."""
import tempfile
import unittest

from fastapi.testclient import TestClient
from app.server import create_app


class HttpBoundary(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.app = create_app(self.temp.name,access_key="synthetic-test-key-000000",max_upload_mib=1,run_scheduler=False)
        self.client = TestClient(self.app)

    def tearDown(self):
        self.client.close()
        self.temp.cleanup()

    def login(self):
        response = self.client.post("/api/login",json=dict(key="synthetic-test-key-000000"))
        self.assertEqual(response.status_code,200)
        self.assertIn("HttpOnly",response.headers["set-cookie"])
        self.assertIn("SameSite=strict",response.headers["set-cookie"])

    def test_auth_csrf_upload_limit_filename_and_logout(self):
        self.assertEqual(self.client.get("/api/files").status_code,401)
        self.assertEqual(self.client.post("/api/login",json=dict(key="incorrect")).status_code,401)
        self.login()
        raw = b"\0\1binary data\xff"
        response = self.client.post("/api/files",params=dict(name="../../events.bin"),content=raw)
        self.assertEqual(response.status_code,201)
        record = response.json()
        self.assertEqual(record["name"],"events.bin")
        self.assertEqual(self.client.get(f"/api/files/{record['id']}/download").content,raw)
        self.assertEqual(self.client.post("/api/files",content=b"x"*(1024*1024+1)).status_code,413)
        self.assertEqual(self.client.post("/api/examples",headers=dict(Origin="https://foreign.example")).status_code,403)
        self.client.post("/api/logout")
        self.assertEqual(self.client.get("/api/tasks").status_code,401)

    def test_experiment_export_parameter_validation_and_presets(self):
        self.login()
        file = self.client.post("/api/files",params=dict(name="=malicious.csv"),content=b"123123123").json()
        payload = dict(name="HTTP check",file_ids=[file["id"]],pipelines=[dict(label="=label",steps=[dict(method="gzip",params={"level":6})])])
        response = self.client.post("/api/experiments",json=payload)
        self.assertEqual(response.status_code,201)
        experiment = response.json()["id"]
        self.assertEqual(len(self.client.get("/api/tasks").json()),1)
        csv = self.client.get(f"/api/experiments/{experiment}/export")
        self.assertEqual(csv.status_code,200)
        self.assertIn("'=malicious.csv",csv.text)
        exported = self.client.get(f"/api/experiments/{experiment}/export?format=json").json()
        self.assertEqual(exported["name"],"HTTP check")
        payload["pipelines"][0]["steps"][0]["params"]["level"] = 999
        self.assertEqual(self.client.post("/api/experiments",json=payload).status_code,400)
        self.assertEqual(len(self.client.get("/api/tasks").json()),1,"Invalid request must not add tasks")
        self.assertEqual(self.client.delete(f"/api/files/{file['id']}").status_code,400)
        self.assertEqual(self.client.patch("/api/settings",json=dict(workers=0)).status_code,422)


if __name__ == "__main__":
    unittest.main()
