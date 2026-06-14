# CLAUDE.md

Guidance for working in this repo efficiently. For full prose docs see [README.md](README.md).

## What this is

.NET 8 AWS Lambda (container image) that scrapes "The Last Supper" (Vivaticket) ticket
availability and sends Telegram notifications when new dates appear. **Notification-only** —
no purchase/sniping/checkout automation.

Runs on a schedule (EventBridge) or manual trigger; the same flow can run locally via `Program.cs`.

## Layout

| File | Role |
|------|------|
| `Function.cs` | Lambda entry point (`FunctionHandler`) — orchestrates the whole run |
| `Program.cs` | Local test runner for the same flow |
| `ConfigManager.cs` | Loads config from `.env` / environment variables (singleton `ConfigManager.Instance`) |
| `Services/TicketScraperService.cs` | Scraping — Playwright (Chromium) + HTML parsing |
| `Services/AvailabilityStateService.cs` | Reads/writes previous available dates in DynamoDB (dedup state) |
| `Services/TelegramNotificationService.cs` | Sends Telegram messages |
| `serverless.yaml` | CloudFormation/SAM template (Lambda, IAM, DynamoDB table, EventBridge rules) |
| `build-for-lambda.ps1` | One-command build + deploy orchestration |
| `Dockerfile` | Container image (base `public.ecr.aws/lambda/dotnet:8`, x86_64) |

## Key facts

- **Region**: `ap-northeast-1`
- **Stack**: `TheLastSupperTicket-Stack`
- **Lambda function**: `the-last-supper-ticket`
- **ECR repo**: `thelastsupperticket`
- **Schedule**: EventBridge cron in `serverless.yaml` (param `ScheduleExpression`, currently `cron(0/30 * * * ? *)` = every 30 min)
- **Playwright on Lambda must be a container image** (`PackageType: Image`), not a zip.
- `aws-lambda-tools-defaults.json` references `serverless.template`, but the real template is `serverless.yaml` (used by `build-for-lambda.ps1`).

## Common tasks

### Build / run locally
```powershell
dotnet build
dotnet run        # runs Program.cs against config in .env
```

### Deploy
Always done via `build-for-lambda.ps1` (defaults: region ap-northeast-1, stack TheLastSupperTicket-Stack):
```powershell
pwsh .\build-for-lambda.ps1                 # build image + update Lambda + sync .env + smoke test
pwsh .\build-for-lambda.ps1 -DeployStack    # CloudFormation first, then image/code update
pwsh .\build-for-lambda.ps1 -DeployStackOnly # CloudFormation only (infra/template changes, no rebuild)
```
- Changed `serverless.yaml` (schedule, IAM, table)? Use `-DeployStack` or `-DeployStackOnly`.
- Changed only C# code? Plain `pwsh .\build-for-lambda.ps1` is enough.

### Verify
```powershell
# Confirm schedule rule
aws events list-rules --region ap-northeast-1 --query "Rules[?ScheduleExpression!=null].[Name,ScheduleExpression,State]" --output table

# Invoke + logs
aws lambda invoke --function-name the-last-supper-ticket --region ap-northeast-1 --cli-binary-format raw-in-base64-out --payload '{}' response.json
aws logs tail /aws/lambda/the-last-supper-ticket --since 5m --region ap-northeast-1
```

## Configuration (env vars / `.env`)

See [.env.example](.env.example) for the template. Notable ones:
- `TARGET_CONFIGS` (`ticketType|url`, multiple separated by `;`) — primary target list
- `TARGET_URLS` / `TARGET_URL` — backward-compatible fallbacks
- `TELEGRAM_BOT_TOKEN`, `TELEGRAM_USER_IDS`, `TELEGRAM_ENABLED`
- `NOTIFY_TARGET_DATES` — optional date filter (`dd/MM/yyyy`, comma-separated)
- `DYNAMODB_STATE_TABLE` — dedup state table

`.env` sync to Lambda is merge-mode (overwrites matching keys, keeps the rest). `.env` is gitignored — never commit secrets.

## Conventions

- Commit only when asked. Branch is `master`; push to `origin` (GitHub: `bustta/thelastsupperticket`).
- Keep changes notification-only — do not add purchase/automation flows.
- When changing the schedule, edit `serverless.yaml` `ScheduleExpression` only; README/diagrams describe it generically and don't hardcode the interval.
