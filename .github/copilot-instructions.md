# The Last Supper Ticket - .NET Core AWS Lambda

## Project Overview
This is a .NET 8 application that scrapes ticket availability for The Last Supper and deploys to AWS Lambda.

## Project Structure

- **Function.cs** - AWS Lambda entry point that handles the main execution flow
- **Services/TicketScraperService.cs** - Core scraping service using HtmlAgilityPack to parse HTML
- **Program.cs** - Local test runner for development and debugging

## Core Features

1. ✅ Scrapes available dates from the target URL
2. ✅ AWS Lambda support (dotnet8 runtime)
3. ✅ Scheduled triggers via CloudWatch Events
4. ✅ Local development and testing support

## Tech Stack

- **.NET 8** - Runtime environment
- **AWS Lambda** - Serverless compute platform
- **HtmlAgilityPack** - HTML parser
- **AngleSharp** - Optional alternative HTML parser

## NuGet Packages Used

- Amazon.Lambda.Core - AWS Lambda core library
- Amazon.Lambda.APIGatewayEvents - API Gateway event support
- HtmlAgilityPack - HTML document parsing
- AngleSharp - CSS selector support

## Development Guide

### Modify Scraping Logic

Edit the `ParseTicketAvailability` method in `Services/TicketScraperService.cs`:

```csharp
// Adjust selectors based on the website HTML structure
var dateNodes = doc.DocumentNode.SelectNodes("//your-selector");
```

### Modify Target URL

Edit the `TargetUrl` constant in `Function.cs`.

### Modify Execution Schedule

Edit the CloudWatch Events rule in `serverless.template`.

## Deployment Steps

1. Install .NET 8 SDK
2. Install AWS Lambda Tools: `dotnet tool install -g Amazon.Lambda.Tools`
3. Configure AWS CLI: `aws configure`
4. Deploy: `dotnet lambda deploy-function TicketScraper`

## Monitoring

- CloudWatch Logs: `/aws/lambda/ticket-scraper`
- Lambda Dashboard: AWS Console

## Completed Checklist

- ✅ .NET project structure
- ✅ AWS Lambda configuration
- ✅ HtmlAgilityPack scraper
- ✅ Local test runner
- ✅ CloudFormation template
- ✅ README and documentation

## Next Steps

1. Set up a testing environment
2. Add database support (DynamoDB/RDS)
3. Implement notifications (SNS)
4. Build a result query API
5. Performance optimization and monitoring

---

Last updated: 2026-02-25
