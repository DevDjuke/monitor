from pathlib import Path

replacements = {
    Path('.github/workflows/otlp-ci.yml'): (
        """          unsupported_media_status=$(curl -sS -o /dev/null -w '%{http_code}' \\
            -X POST \\
            -H 'X-Monitor-Key: ci-otlp-ingestion-key' \\
            -H 'Content-Type: application/json' \\
            -d '{}' \\
            http://127.0.0.1:5082/v1/traces)
          test \"$unsupported_media_status\" = \"415\"""",
        """          unsupported_media_status=$(curl -sS -o /dev/null -w '%{http_code}' \\
            -X POST \\
            -H 'X-Monitor-Key: ci-otlp-ingestion-key' \\
            -H 'Content-Type: application/xml' \\
            -d '<unsupported />' \\
            http://127.0.0.1:5082/v1/traces)
          test \"$unsupported_media_status\" = \"415\""""
    ),
    Path('.github/workflows/logs-ci.yml'): (
        """          unsupported_logs=$(curl -sS -o /dev/null -w '%{http_code}' \\
            -X POST \\
            -H 'X-Monitor-Key: ci-logs-ingestion-key' \\
            -H 'Content-Type: application/json' \\
            -d '{}' \\
            http://127.0.0.1:5086/v1/logs)
          test \"$unsupported_logs\" = \"415\"""",
        """          unsupported_logs=$(curl -sS -o /dev/null -w '%{http_code}' \\
            -X POST \\
            -H 'X-Monitor-Key: ci-logs-ingestion-key' \\
            -H 'Content-Type: application/xml' \\
            -d '<unsupported />' \\
            http://127.0.0.1:5086/v1/logs)
          test \"$unsupported_logs\" = \"415\""""
    ),
    Path('.github/scripts/otlp-metrics-ci.sh'): (
        """json_status=$(curl -sS -o /dev/null -w '%{http_code}' \\
  -H \"X-Monitor-Key: $matching_key\" \\
  -H 'Content-Type: application/json' \\
  -d '{}' \"$BASE_URL/v1/metrics\")
test \"$json_status\" = \"415\"""",
        """unsupported_media_status=$(curl -sS -o /dev/null -w '%{http_code}' \\
  -H \"X-Monitor-Key: $matching_key\" \\
  -H 'Content-Type: application/xml' \\
  -d '<unsupported />' \"$BASE_URL/v1/metrics\")
test \"$unsupported_media_status\" = \"415\""""
    )
}

changed = False
for path, (old, new) in replacements.items():
    text = path.read_text(encoding='utf-8')
    if old in text:
        path.write_text(text.replace(old, new, 1), encoding='utf-8')
        print(f'updated {path}')
        changed = True
    elif new in text:
        print(f'already updated {path}')
    else:
        raise SystemExit(f'expected legacy assertion not found in {path}')

Path('/tmp/p14-changed').write_text('1' if changed else '0', encoding='utf-8')
