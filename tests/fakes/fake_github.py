#!/usr/bin/env python3
"""In-memory fake of the GitHub REST/GraphQL endpoints Cascade uses, plus an OpenAI-compatible
chat endpoint. Lets the full seed -> sync -> propose -> apply -> teardown loop run end to end
without a token or network access.

    python3 tests/fakes/fake_github.py 8790

Then launch Cascade with GITHUB_API_URL=http://127.0.0.1:8790, GITHUB_TOKEN=fake,
LLM_URL=http://127.0.0.1:8790/v1/chat/completions, LLM_TOKEN=fake.
"""
import json
import re
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

LOCK = threading.Lock()
STATE = {"issues": {}, "labels": {}, "next": 1, "sub_issues": [], "deleted": 0}


def issue_json(owner_repo, n, issue):
    return {
        "number": n,
        "id": 100000 + n,
        "node_id": f"I_{n}",
        "title": issue["title"],
        "body": issue["body"],
        "state": issue["state"],
        "labels": [{"name": label} for label in issue["labels"]],
        "assignees": [],
        "html_url": f"https://github.com/{owner_repo}/issues/{n}",
        "created_at": "2026-10-24T12:00:00Z",
        "updated_at": issue.get("updated_at", "2026-10-24T12:00:00Z"),
        "milestone": None,
    }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def send(self, status, payload, headers=None):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}")

    def authorised(self):
        if not (self.headers.get("Authorization") or "").startswith("Bearer "):
            self.send(401, {"message": "Bad credentials"})
            return False
        return True

    def do_GET(self):
        if not self.authorised():
            return
        url = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(url.query).items()}
        path = url.path
        if path == "/user":
            return self.send(200, {"login": "fake-anthony", "name": "Fake User", "html_url": "https://github.com/fake"})
        if path == "/user/repos":
            return self.send(200, [{"full_name": "sun-of-anton/cascade-sandbox", "html_url": "https://github.com/sun-of-anton/cascade-sandbox"}])
        m = re.match(r"^/repos/([^/]+/[^/]+)(/.*)?$", path)
        if not m:
            return self.send(404, {"message": "Not Found"})
        repo, rest = m.group(1), m.group(2) or ""
        with LOCK:
            if rest == "":
                return self.send(200, {"full_name": repo, "id": abs(hash(repo.lower())) % 10**9})
            if rest == "/labels":
                return self.send(200, [{"name": n} for n in sorted(STATE["labels"])])
            if rest == "/issues":
                wanted = [x for x in (q.get("labels") or "").split(",") if x]
                state = q.get("state", "open")
                rows = []
                for n in sorted(STATE["issues"], reverse=True):
                    issue = STATE["issues"][n]
                    if state != "all" and issue["state"] != state:
                        continue
                    if any(w not in issue["labels"] for w in wanted):
                        continue
                    rows.append(issue_json(repo, n, issue))
                per_page = int(q.get("per_page", 30))
                page = int(q.get("page", 1))
                chunk = rows[(page - 1) * per_page: page * per_page]
                headers = {}
                if page * per_page < len(rows):
                    nq = dict(q, page=str(page + 1))
                    nxt = f"http://{self.headers['Host']}{path}?" + "&".join(f"{k}={v}" for k, v in nq.items())
                    headers["Link"] = f'<{nxt}>; rel="next"'
                return self.send(200, chunk, headers)
            mi = re.match(r"^/issues/(\d+)$", rest)
            if mi:
                n = int(mi.group(1))
                if n not in STATE["issues"]:
                    return self.send(404, {"message": "Not Found"})
                return self.send(200, issue_json(repo, n, STATE["issues"][n]))
        return self.send(404, {"message": "Not Found"})

    def do_PATCH(self):
        if not self.authorised():
            return
        m = re.match(r"^/repos/([^/]+/[^/]+)/issues/(\d+)$", urlparse(self.path).path)
        if not m:
            return self.send(404, {"message": "Not Found"})
        data = self.body()
        n = int(m.group(2))
        with LOCK:
            issue = STATE["issues"].get(n)
            if not issue:
                return self.send(404, {"message": "Not Found"})
            for key in ("title", "body", "state"):
                if key in data:
                    issue[key] = data[key]
            if "labels" in data:
                issue["labels"] = list(data["labels"])
            issue["updated_at"] = "2026-10-24T13:00:00Z"
            return self.send(200, issue_json(m.group(1), n, issue))

    def do_POST(self):
        if not self.authorised():
            return
        path = urlparse(self.path).path
        data = self.body()
        if path == "/graphql":
            node = (data.get("variables") or {}).get("id", "")
            with LOCK:
                n = int(node[2:]) if node.startswith("I_") and node[2:].isdigit() else -1
                if data.get("query", "").lstrip().startswith("query"):
                    found = {"number": n, "repository": {"nameWithOwner": "sun-of-anton/cascade-sandbox"}} if n in STATE["issues"] else None
                    return self.send(200, {"data": {"node": found}})
                if n not in STATE["issues"]:
                    return self.send(200, {"errors": [{"message": f"Could not resolve to a node with the global id of '{node}'"}]})
                del STATE["issues"][n]
                STATE["deleted"] += 1
            return self.send(200, {"data": {"deleteIssue": {"clientMutationId": None}}})
        if path == "/v1/chat/completions":
            prompt = "\n".join(m.get("content", "") for m in data.get("messages", []))
            ids = re.findall(r"### id: (\S+)", prompt)
            effort = "effort" in prompt.split("\n")[0].lower() or "FEATURE below" in prompt
            out = []
            for i, ident in enumerate(ids):
                if effort:
                    out.append({"id": ident, "effort": ["S", "M", "L", "XL"][i % 4], "confidence": "high", "reason": "fake model"})
                else:
                    out.append({"id": ident, "points": [3, 5, 13, 2][i % 4], "confidence": ["high", "medium"][i % 2], "reason": "fake model"})
            return self.send(200, {"choices": [{"message": {"content": "```json\n" + json.dumps(out) + "\n```"}}], "usage": {"prompt_tokens": len(prompt) // 4, "completion_tokens": 50}})
        m = re.match(r"^/repos/([^/]+/[^/]+)(/.*)$", path)
        if not m:
            return self.send(404, {"message": "Not Found"})
        repo, rest = m.group(1), m.group(2)
        with LOCK:
            if rest == "/labels":
                if data["name"] in STATE["labels"]:
                    return self.send(422, {"message": "Validation Failed", "errors": [{"code": "already_exists"}]})
                STATE["labels"][data["name"]] = data
                return self.send(201, data)
            if rest == "/issues":
                n = STATE["next"]
                STATE["next"] += 1
                labels = list(data.get("labels") or [])
                for label in labels:
                    STATE["labels"].setdefault(label, {"name": label})
                STATE["issues"][n] = {"title": data["title"], "body": data.get("body", ""), "labels": labels, "state": "open"}
                return self.send(201, issue_json(repo, n, STATE["issues"][n]))
            ms = re.match(r"^/issues/(\d+)/sub_issues$", rest)
            if ms:
                STATE["sub_issues"].append((int(ms.group(1)), data.get("sub_issue_id")))
                return self.send(201, {"ok": True})
        return self.send(404, {"message": "Not Found"})


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8790
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"fake GitHub on http://127.0.0.1:{port}", flush=True)
    server.serve_forever()
