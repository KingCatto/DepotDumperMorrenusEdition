using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepotDumper
{
    public static class ReportGenerator
    {
        public static void SaveAllReports(OperationSummary summary, string reportsDirectory)
        {
            Directory.CreateDirectory(reportsDirectory);

            try
            {
                string htmlContent = HtmlReportGenerator.GenerateSpaceHtmlReport(summary);
                File.WriteAllText(Path.Combine(reportsDirectory, "report.html"), htmlContent);

                SaveTextSummary(summary, Path.Combine(reportsDirectory, "summary.txt"));

                SaveAppsCsv(summary, Path.Combine(reportsDirectory, "apps.csv"));

                SaveJsonReport(summary, Path.Combine(reportsDirectory, "full_report.json"));

                Console.WriteLine($"All reports saved to {reportsDirectory}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating reports: {ex.Message}");
            }
        }

        private static void SaveTextSummary(OperationSummary summary, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== DepotDumper Operation Summary ===");
            sb.AppendLine($"Start Time: {summary.StartTime}");
            sb.AppendLine($"End Time: {summary.EndTime}");
            sb.AppendLine($"Duration: {FormatTimeSpan(summary.Duration)}");
            sb.AppendLine();
            sb.AppendLine($"Apps: {summary.SuccessfulApps} successful, {summary.FailedApps} failed (Total Parent Groups Tracked: {summary.TotalAppsProcessed})"); // Using updated count description
            sb.AppendLine($"Depots: {summary.SuccessfulDepots} successful, {summary.FailedDepots} failed (Total Depots Tracked: {summary.TotalDepotsProcessed})"); // Using updated count description
            sb.AppendLine($"Manifests: {summary.NewManifestsDownloaded} downloaded, {summary.ManifestsSkipped} skipped, {summary.FailedManifests} failed (Total Manifests Tracked: {summary.TotalManifestsProcessed})"); // Using updated count description
            sb.AppendLine();
            sb.AppendLine("=== Apps Processed ===");
            foreach (var app in summary.AppSummaries)
            {
                sb.AppendLine($"App {app.AppId} ({app.AppName}): {(app.ComputedSuccess ? "Success" : "Failed")}");
                sb.AppendLine($"  Last Updated: {app.LastUpdated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}");
                sb.AppendLine($"  Depots: {app.ProcessedDepots}/{app.TotalDepots} (Skipped: {app.SkippedDepots})");
                sb.AppendLine($"  Manifests: {app.NewManifests} new, {app.SkippedManifests} skipped");
                if (app.AppErrors.Count > 0)
                {
                    sb.AppendLine($"  Errors: {app.AppErrors.Count}");

                    foreach (var error in app.AppErrors.Take(5))
                    {
                        sb.AppendLine($"    - {error}");
                    }
                    if (app.AppErrors.Count > 5)
                    {
                        sb.AppendLine($"    ... and {app.AppErrors.Count - 5} more errors");
                    }
                }
                sb.AppendLine();
            }

            if (summary.Errors.Count > 0)
            {
                sb.AppendLine("=== Errors ===");
                sb.AppendLine($"Total Errors: {summary.Errors.Count}");
                var errorGroups = summary.Errors
                    .GroupBy(error => GetErrorType(error))
                    .OrderByDescending(g => g.Count());

                foreach (var group in errorGroups)
                {
                    sb.AppendLine($"{group.Key}: {group.Count()} occurrences");

                    foreach (var error in group.Take(3))
                    {
                        sb.AppendLine($"  - {error}");
                    }
                    if (group.Count() > 3)
                    {
                        sb.AppendLine($"  ... and {group.Count() - 3} more similar errors");
                    }
                    sb.AppendLine();
                }
            }

            File.WriteAllText(path, sb.ToString());
        }

        private static void SaveAppsCsv(OperationSummary summary, string path)
        {
            var sb = new StringBuilder();

            sb.AppendLine("AppId,AppName,LastUpdated,TotalDepots,ProcessedDepots,SkippedDepots,NewManifests,SkippedManifests,Status,ErrorCount");

            foreach (var app in summary.AppSummaries)
            {
                string statusText = app.ComputedSuccess ? "Success" : "Failed";
                string lastUpdatedText = app.LastUpdated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                sb.AppendLine($"{app.AppId},\"{EscapeCsvField(app.AppName)}\",\"{lastUpdatedText}\",{app.TotalDepots},{app.ProcessedDepots},{app.SkippedDepots},{app.NewManifests},{app.SkippedManifests},{statusText},{app.AppErrors.Count}");
            }

            File.WriteAllText(path, sb.ToString());
        }


        private static void SaveJsonReport(OperationSummary summary, string path)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            string json = JsonSerializer.Serialize(summary, options);
            File.WriteAllText(path, json);
        }


        private static string GetErrorType(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return "Unknown";

            if (errorMessage.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return "Network Error";

            if (errorMessage.Contains("file", StringComparison.OrdinalIgnoreCase) &&
                (errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("permission", StringComparison.OrdinalIgnoreCase)))
                return "File Access Error";

            if (errorMessage.Contains("manifest", StringComparison.OrdinalIgnoreCase) &&
                errorMessage.Contains("download", StringComparison.OrdinalIgnoreCase))
                return "Manifest Download Error";

            if (errorMessage.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("login", StringComparison.OrdinalIgnoreCase))
                return "Authentication Error";

            if (errorMessage.Contains("database", StringComparison.OrdinalIgnoreCase))
                return "Database Error";

            return "Other Error";
        }


        public static string FormatTimeSpan(TimeSpan span)
        {
            if (span.TotalDays >= 1)
                return $"{span.Days}d {span.Hours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalHours >= 1)
                return $"{span.Hours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalMinutes >= 1)
                return $"{span.Minutes}m {span.Seconds}s";
            return $"{span.Seconds}.{span.Milliseconds / 10}s";
        }


        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            if (field.Contains("\"") || field.Contains(",") || field.Contains("\n"))
            {

                field = field.Replace("\"", "\"\"");

                return $"\"{field}\"";
            }

            return field;
        }
    }
}