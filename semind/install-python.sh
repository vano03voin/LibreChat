#!/bin/bash
set -euo pipefail
python=/pkgs/python/3.14.4/bin/python3
mkdir -p /pkgs/.tools
if [ ! -x /pkgs/.tools/uv ]; then cp /pkgs/python/3.14.4/bin/uv /pkgs/.tools/uv; fi
uv=/pkgs/.tools/uv
test -x "$python"
test -f /pkgs/python/3.14.4/lib/python3.14/.sandbox-pipe-runtime-v1
export UV_CACHE_DIR=/cache
if [ ! -f /profile/python.lock ]; then
  "$uv" pip compile --python "$python" /profile/python.in --generate-hashes -o /profile/python.lock
fi
"$uv" pip sync --python "$python" --require-hashes /profile/python.lock
"$python" -c 'import numpy, pandas, matplotlib, scipy, statsmodels, plotly, seaborn; print("Python package imports passed")'
date +%s000 > /pkgs/python/3.14.4/.package-installed
chmod -R a+rX /pkgs/python/3.14.4
echo 'User-selected calculation and chart libraries installed; versions frozen in python.lock.'
