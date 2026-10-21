#Requires -Version 7.0
# Copy this file to secrets.local.ps1 and fill in real values.
# secrets.local.ps1 is gitignored and is dot-sourced by Publish-SingleExe.ps1.
# Leave a value empty to skip it.

$env:PCL_CURSEFORGE_API_KEY = ''       # required
$env:PCL_MS_CLIENT_ID = ''             # required for Microsoft login
$env:PCL_MS_CLIENT_SECRET = ''         # optional
$env:PCL_NAID_CLIENT_ID = ''           # optional
$env:PCL_NAID_CLIENT_SECRET = ''       # optional
$env:PCL_LINK_SERVER_ROOT = ''         # optional, multiple roots separated by '|'
$env:PCL_TELEMETRY_KEY = ''            # optional
$env:PCL_SENTRY_DSN = ''               # optional
