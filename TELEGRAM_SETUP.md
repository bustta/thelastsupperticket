# Telegram Notification Setup Guide

## Overview

When the scraper detects available tickets, the system automatically sends Telegram notifications to configured recipients.

## Setup Steps

### 1. Create a Telegram Bot

1. Search for **@BotFather** in Telegram and open the chat
2. Send the command `/newbot`
3. Follow the prompts to set your bot name and username
4. BotFather will return a **Bot Token** in a format like: `123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11`

> ⚠️ **Important**: Keep your Bot Token private. Never share it.

### 2. Get User ID or Username

#### ✅ Option A: Use Numeric User ID (Recommended, Most Reliable)

1. Search for **@userinfobot** in Telegram and open the chat
2. Send any message
3. The bot will reply with your numeric user ID (for example: `1234567890`)

#### ⚠️ Option B: Use Username (Requires Conditions)

> **Note**: Username-based delivery requires these conditions:
> - The username must be public
> - Some bot settings do not support sending messages by username
> - If username delivery fails, numeric user ID usually works

1. Open Telegram settings
2. Find your username (set one first if you do not have it)
3. Username format: `@yourusername`

### 3. Set Environment Variables

#### Local Development (Windows PowerShell)

**Option 1: Numeric User ID (Recommended)**
```powershell
$env:TELEGRAM_ENABLED = "true"
$env:TELEGRAM_BOT_TOKEN = "YOUR_BOT_TOKEN"
$env:TELEGRAM_USER_IDS = "1234567890"  # Replace with your numeric user ID
$env:NOTIFY_TARGET_DATES = "09/04/2026,10/04/2026"  # Optional: notify only when these dates become newly available (dd/MM/yyyy)
```

**Option 2: Username (Requires Conditions)**
```powershell
$env:TELEGRAM_ENABLED = "true"
$env:TELEGRAM_BOT_TOKEN = "YOUR_BOT_TOKEN"
$env:TELEGRAM_USER_IDS = "@yourusername"  # Username must be public
```

**Option 3: Mixed Recipients**
```powershell
$env:TELEGRAM_ENABLED = "true"
$env:TELEGRAM_BOT_TOKEN = "YOUR_BOT_TOKEN"
$env:TELEGRAM_USER_IDS = "@yourusername,123456789,@friend"
```

#### Local Development (Command Prompt)

```cmd
set TELEGRAM_ENABLED=true
set TELEGRAM_BOT_TOKEN=YOUR_BOT_TOKEN
set TELEGRAM_USER_IDS=@yourusername,123456789
set NOTIFY_TARGET_DATES=09/04/2026,10/04/2026
```

#### AWS Lambda Deployment

In the AWS Lambda Console, go to Configuration → Environment variables and add:

| Environment Variable | Value | Description |
|---------|-----|------|
| `TELEGRAM_ENABLED` | `true` | Enable Telegram notifications |
| `TELEGRAM_BOT_TOKEN` | `YOUR_BOT_TOKEN` | Telegram Bot Token |
| `TELEGRAM_USER_IDS` | `@yourusername,123456789` | Recipient usernames or user IDs (comma-separated) |
| `NOTIFY_TARGET_DATES` | `09/04/2026,10/04/2026` | Optional; send "new availability" notifications only when target dates match (strict `dd/MM/yyyy`) |

### 4. Test the Configuration

Run a local test to verify your setup:

```bash
dotnet run
```

If everything is configured correctly, you should see:
- `✓ Telegram message sent to user XXXXXX` (when tickets are available)

## Environment Variable Details

### TELEGRAM_ENABLED
- **Type**: boolean (`true`/`false`)
- **Default**: `false`
- **Description**: Enables or disables Telegram notifications

### TELEGRAM_BOT_TOKEN
- **Type**: string
- **Default**: empty string
- **Description**: Telegram Bot Token (required)
- **Format**: `123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11`

### TELEGRAM_USER_IDS
- **Type**: string
- **Default**: empty string
- **Description**: Recipient Telegram usernames or user IDs (comma-separated)
- **Examples**:
   - Numeric IDs (recommended): `1234567890` or `123456789,987654321` ✅ most reliable
  - Usernames: `@yourusername` or `@friend1,@friend2` ⚠️ requires conditions
   - Mixed: `1234567890,@publicusername` (numeric IDs still work if username delivery fails)

### NOTIFY_TARGET_DATES
- **Type**: string
- **Default**: empty string
- **Description**: Optional target-date allowlist; sends Telegram "new availability" only when matched dates become newly available
- **Example**: `09/04/2026,10/04/2026`
- **Format rule**: accepts only `dd/MM/yyyy` (for example `09/04/2026`)
- **Additional note**: if unset or empty, default behavior remains unchanged (notify for any newly available date)

## Notification Message Format

When tickets are available, the Telegram message includes:

```
🎉 Tickets Available!

Available Dates:
  • 2026-03-15
  • 2026-03-22
  • 2026-04-10

📌 Ticket Page: https://...

Time: 2026-02-25 15:30:45
```

## FAQ

### Q: Why am I not receiving notifications?

**Checklist:**
1. Confirm `TELEGRAM_ENABLED` is set to `true`
2. Confirm `TELEGRAM_BOT_TOKEN` is correct
3. Confirm recipient entries in `TELEGRAM_USER_IDS` are correct
4. Check CloudWatch logs for errors
5. Ensure the Telegram bot has been started and is allowed to send messages

### Q: Why does username delivery fail while numeric ID works?

**Answer**: This is a common Telegram Bot API limitation:

1. **Username delivery requires specific conditions**
   - Username must be public
   - Some bot token/settings combinations cannot send to usernames
   - Some API paths support user IDs but not usernames

2. **Numeric IDs are more reliable**
   - Numeric IDs are Telegram's standard identifier
   - All bots support user IDs
   - Not affected by username privacy/visibility settings

3. **Recommended solution**
   - ✅ Use numeric IDs (recommended)
   - ⚠️ If you use usernames, make sure they are fully public
   - 📌 Mixed mode is supported: `1234567890,@publicusername`

### Q: What if I forgot my Bot Token?

Contact @BotFather again, use `/mybots` to find your bot, then run `/token` to regenerate the token.

### Q: How do I notify multiple recipients?

Use comma-separated values in `TELEGRAM_USER_IDS`:

**Option 1: Multiple numeric IDs (recommended)**
```
TELEGRAM_USER_IDS=1234567890,123456789,987654321
```

**Option 2: Multiple usernames (all must be public)**
```
TELEGRAM_USER_IDS=@alice,@bob,@charlie
```

**Option 3: Mixed mode**
```
TELEGRAM_USER_IDS=1234567890,@publicusername,123456789,@friend
```

### Q: What happens if an ID or username is invalid?

The system logs the error, but scraping continues normally. It will still attempt delivery to all valid recipients.

## API Limits

- Telegram Bot API allows roughly up to ~30 messages per second per chat
- Very large recipient lists may hit rate limits
- The system automatically retries failed sends

## Security Recommendations

1. **Do not hardcode** bot tokens or user IDs in source code; always use environment variables
2. **Use AWS Secrets Manager** for sensitive values
3. Grant Lambda only the IAM permissions needed for configuration/secrets access

## Related Resources

- [Telegram Bot API Docs](https://core.telegram.org/bots/api)
- [Telegram.Bot C# Library](https://github.com/TelegramBots/Telegram.Bot)
- [BotFather Commands](https://core.telegram.org/bots#botfather)

---

**Last updated**: 2026-03-01
