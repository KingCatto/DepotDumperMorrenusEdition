using System;
using System.Linq;
using System.Text;

namespace DepotDumper
{
    public static class HtmlReportGenerator
    {
        public static string GenerateSpaceHtmlReport(OperationSummary summary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("    <meta charset=\"UTF-8\">");
            sb.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine("    <title>DepotDumper Operation Report</title>");
            sb.AppendLine("    <style>");
            sb.AppendLine("        :root {");
            sb.AppendLine("            --background-dark: #0a0e17;");
            sb.AppendLine("            --background-medium: #13182c;");
            sb.AppendLine("            --accent-blue: #3498db;");
            sb.AppendLine("            --accent-purple: #9b59b6;");
            sb.AppendLine("            --text-bright: #ecf0f1;");
            sb.AppendLine("            --text-muted: #95a5a6;");
            sb.AppendLine("            --successful: #2ecc71;");
            sb.AppendLine("            --warning: #f39c12;");
            sb.AppendLine("            --error: #e74c3c;");
            sb.AppendLine("        }");
            sb.AppendLine("        body {");
            sb.AppendLine("            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;");
            sb.AppendLine("            background-color: var(--background-dark);");
            sb.AppendLine("            color: var(--text-bright);");
            sb.AppendLine("            margin: 0;");
            sb.AppendLine("            padding: 0;");
            sb.AppendLine("            background-image: radial-gradient(circle at top right, rgba(23, 43, 77, 0.7) 0%, rgba(10, 14, 23, 0.7) 100%);");
            sb.AppendLine("            background-attachment: fixed;");
            sb.AppendLine("            position: relative;");
            sb.AppendLine("            min-height: 100vh;");
            sb.AppendLine("        }");
            sb.AppendLine("        body::before {");
            sb.AppendLine("            content: '';");
            sb.AppendLine("            position: absolute;");
            sb.AppendLine("            top: 0;");
            sb.AppendLine("            left: 0;");
            sb.AppendLine("            right: 0;");
            sb.AppendLine("            bottom: 0;");
            sb.AppendLine("            background: url('data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0MDAiIGhlaWdodD0iNDAwIj48cmVjdCB3aWR0aD0iNDAwIiBoZWlnaHQ9IjQwMCIgZmlsbD0idHJhbnNwYXJlbnQiPjwvcmVjdD48Y2lyY2xlIGN4PSIxMCIgY3k9IjEwIiByPSIxLjUiIGZpbGw9IiNmZmYiIG9wYWNpdHk9IjAuMyI+PC9jaXJjbGU+PGNpcmNsZSBjeD0iNDAiIGN5PSI0MCIgcj0iMSIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC40Ij48L2NpcmNsZT48Y2lyY2xlIGN4PSI3MCIgY3k9IjIwIiByPSIyIiBmaWxsPSIjZmZmIiBvcGFjaXR5PSIwLjMiPjwvY2lyY2xlPjxjaXJjbGUgY3g9IjEwMCIgY3k9IjUwIiByPSIxLjgiIGZpbGw9IiNmZmYiIG9wYWNpdHk9IjAuMiI+PC9jaXJjbGU+PGNpcmNsZSBjeD0iMTMwIiBjeT0iMTAiIHI9IjEuMiIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC40Ij48L2NpcmNsZT48Y2lyY2xlIGN4PSIxNjAiIGN5PSI0MCIgcj0iMSIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC4yIj48L2NpcmNsZT48Y2lyY2xlIGN4PSIxOTAiIGN5PSIyMCIgcj0iMS41IiBmaWxsPSIjZmZmIiBvcGFjaXR5PSIwLjMiPjwvY2lyY2xlPjxjaXJjbGUgY3g9IjIyMCIgY3k9IjUwIiByPSIwLjgiIGZpbGw9IiNmZmYiIG9wYWNpdHk9IjAuMyI+PC9jaXJjbGU+PGNpcmNsZSBjeD0iMjUwIiBjeT0iMTAiIHI9IjEuNyIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC40Ij48L2NpcmNsZT48Y2lyY2xlIGN4PSIyODAiIGN5PSI0MCIgcj0iMSIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC4yIj48L2NpcmNsZT48Y2lyY2xlIGN4PSIzMTAiIGN5PSIyMCIgcj0iMS4yIiBmaWxsPSIjZmZmIiBvcGFjaXR5PSIwLjMiPjwvY2lyY2xlPjxjaXJjbGUgY3g9IjM0MCIgY3k9IjUwIiByPSIwLjkiIGZpbGw9IiNmZmYiIG9wYWNpdHk9IjAuMiI+PC9jaXJjbGU+PGNpcmNsZSBjeD0iMzcwIiBjeT0iMTAiIHI9IjEuMyIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC40Ij48L2NpcmNsZT48Y2lyY2xlIGN4PSIxMCIgY3k9IjgwIiByPSIxIiBmaWxsPSIjZmZmIiBvcGFjaXR5PSIwLjMiPjwvY2lyY2xlPjxjaXJjbGUgY3g9IjQwIiBjeT0iMTEwIiByPSIwLjgiIGZpbGw9IiNmZmYiIG9wYWNpdHk9IjAuNCIgPjwvY2lyY2xlPjxjaXJjbGUgY3g9IjcwIiBjeT0iODAiIHI9IjEuMiIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC4zIj48L2NpcmNsZT48Y2lyY2xlIGN4PSIxMDAiIGN5PSIxMTAiIHI9IjAuNyIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC4yIj48L2NpcmNsZT48Y2lyY2xlIGN4PSIxMzAiIGN5PSI4MCIgcj0iMSIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC40Ij48L2NpcmNsZT48Y2lyY2xlIGN4PSIxNjAiIGN5PSIxMTAiIHI9IjEuMyIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC4yIj48L2NpcmNsZT48Y2lyY2xlIGN4PSIxOTAiIGN5PSI4MCIgcj0iMC44IiBmaWxsPSIjZmZmIiBvcGFjaXR5PSIwLjMiPjwvY2lyY2xlPjxjaXJjbGUgY3g9IjIyMCIgY3k9IjExMCIgcj0iMSIgZmlsbD0iI2ZmZiIgb3BhY2l0eT0iMC4zIj48L2NpcmNsZT48L3N2Zz4=');");
            sb.AppendLine("            z-index: -1;");
            sb.AppendLine("            opacity: 0.4;");
            sb.AppendLine("        }");
            sb.AppendLine("        .container {");
            sb.AppendLine("            max-width: 1200px;");
            sb.AppendLine("            margin: 0 auto;");
            sb.AppendLine("            padding: 30px;");
            sb.AppendLine("        }");
            sb.AppendLine("        header {");
            sb.AppendLine("            text-align: center;");
            sb.AppendLine("            margin-bottom: 40px;");
            sb.AppendLine("            position: relative;");
            sb.AppendLine("            padding-bottom: 20px;");
            sb.AppendLine("        }");
            sb.AppendLine("        h1 {");
            sb.AppendLine("            font-size: 36px;");
            sb.AppendLine("            background: linear-gradient(to right, var(--accent-blue), var(--accent-purple));");
            sb.AppendLine("            -webkit-background-clip: text;");
            sb.AppendLine("            -webkit-text-fill-color: transparent;");
            sb.AppendLine("            margin: 0;");
            sb.AppendLine("            padding: 0;");
            sb.AppendLine("        }");
            sb.AppendLine("        header::after {");
            sb.AppendLine("            content: '';");
            sb.AppendLine("            height: 3px;");
            sb.AppendLine("            width: 100px;");
            sb.AppendLine("            background: linear-gradient(to right, var(--accent-blue), var(--accent-purple));");
            sb.AppendLine("            position: absolute;");
            sb.AppendLine("            bottom: 0;");
            sb.AppendLine("            left: 50%;");
            sb.AppendLine("            transform: translateX(-50%);");
            sb.AppendLine("            border-radius: 3px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .timestamp {");
            sb.AppendLine("            color: var(--text-muted);");
            sb.AppendLine("            margin-top: 8px;");
            sb.AppendLine("            font-size: 14px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .card {");
            sb.AppendLine("            background-color: var(--background-medium);");
            sb.AppendLine("            border-radius: 10px;");
            sb.AppendLine("            box-shadow: 0 4px 15px rgba(0, 0, 0, 0.2);");
            sb.AppendLine("            margin-bottom: 30px;");
            sb.AppendLine("            overflow: hidden;");
            sb.AppendLine("            border: 1px solid rgba(52, 152, 219, 0.2);");
            sb.AppendLine("        }");
            sb.AppendLine("        .card-header {");
            sb.AppendLine("            padding: 15px 20px;");
            sb.AppendLine("            background-color: rgba(52, 152, 219, 0.1);");
            sb.AppendLine("            border-bottom: 1px solid rgba(52, 152, 219, 0.2);");
            sb.AppendLine("            display: flex;");
            sb.AppendLine("            justify-content: space-between;");
            sb.AppendLine("            align-items: center;");
            sb.AppendLine("        }");
            sb.AppendLine("        .card-title {");
            sb.AppendLine("            font-size: 18px;");
            sb.AppendLine("            font-weight: 600;");
            sb.AppendLine("            margin: 0;");
            sb.AppendLine("            color: var(--accent-blue);");
            sb.AppendLine("        }");
            sb.AppendLine("        .card-content {");
            sb.AppendLine("            padding: 20px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-grid {");
            sb.AppendLine("            display: grid;");
            sb.AppendLine("            grid-template-columns: repeat(auto-fill, minmax(250px, 1fr));");
            sb.AppendLine("            gap: 20px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-box {");
            sb.AppendLine("            background-color: rgba(19, 24, 44, 0.8);");
            sb.AppendLine("            border-radius: 8px;");
            sb.AppendLine("            padding: 15px;");
            sb.AppendLine("            text-align: center;");
            sb.AppendLine("            position: relative;");
            sb.AppendLine("            overflow: hidden;");
            sb.AppendLine("            border: 1px solid rgba(52, 152, 219, 0.2);");
            sb.AppendLine("            transition: transform 0.2s ease, box-shadow 0.2s ease;");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-box:hover {");
            sb.AppendLine("            transform: translateY(-3px);");
            sb.AppendLine("            box-shadow: 0 6px 20px rgba(0, 0, 0, 0.3);");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-box::before {");
            sb.AppendLine("            content: '';");
            sb.AppendLine("            position: absolute;");
            sb.AppendLine("            top: 0;");
            sb.AppendLine("            left: 0;");
            sb.AppendLine("            right: 0;");
            sb.AppendLine("            height: 3px;");
            sb.AppendLine("            background: linear-gradient(to right, var(--accent-blue), var(--accent-purple));");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-box.success::before {");
            sb.AppendLine("            background: linear-gradient(to right, #27ae60, #2ecc71);");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-box.warning::before {");
            sb.AppendLine("            background: linear-gradient(to right, #e67e22, #f39c12);");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-box.error::before {");
            sb.AppendLine("            background: linear-gradient(to right, #c0392b, #e74c3c);");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-value {");
            sb.AppendLine("            font-size: 36px;");
            sb.AppendLine("            font-weight: 700;");
            sb.AppendLine("            margin: 10px 0;");
            sb.AppendLine("            color: var(--text-bright);");
            sb.AppendLine("        }");
            sb.AppendLine("        .stat-label {");
            sb.AppendLine("            color: var(--text-muted);");
            sb.AppendLine("            font-size: 14px;");
            sb.AppendLine("            margin-bottom: 5px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .success-value {");
            sb.AppendLine("            color: var(--successful);");
            sb.AppendLine("        }");
            sb.AppendLine("        .warning-value {");
            sb.AppendLine("            color: var(--warning);");
            sb.AppendLine("        }");
            sb.AppendLine("        .error-value {");
            sb.AppendLine("            color: var(--error);");
            sb.AppendLine("        }");
            sb.AppendLine("        .app-table {");
            sb.AppendLine("            width: 100%;");
            sb.AppendLine("            border-collapse: collapse;");
            sb.AppendLine("        }");
            sb.AppendLine("        .app-table th {");
            sb.AppendLine("            text-align: left;");
            sb.AppendLine("            padding: 12px 15px;");
            sb.AppendLine("            background-color: rgba(52, 152, 219, 0.1);");
            sb.AppendLine("            color: var(--accent-blue);");
            sb.AppendLine("            font-weight: 600;");
            sb.AppendLine("            border-bottom: 1px solid rgba(52, 152, 219, 0.2);");
            sb.AppendLine("        }");
            sb.AppendLine("        .app-table td {");
            sb.AppendLine("            padding: 12px 15px;");
            sb.AppendLine("            border-bottom: 1px solid rgba(52, 152, 219, 0.1);");
            sb.AppendLine("        }");
            sb.AppendLine("        .app-table tr:last-child td {");
            sb.AppendLine("            border-bottom: none;");
            sb.AppendLine("        }");
            sb.AppendLine("        .app-table tr:hover td {");
            sb.AppendLine("            background-color: rgba(52, 152, 219, 0.05);");
            sb.AppendLine("        }");
            sb.AppendLine("        .app-status {");
            sb.AppendLine("            display: inline-block;");
            sb.AppendLine("            padding: 4px 8px;");
            sb.AppendLine("            border-radius: 4px;");
            sb.AppendLine("            font-size: 12px;");
            sb.AppendLine("            font-weight: 600;");
            sb.AppendLine("        }");
            sb.AppendLine("        .status-success {");
            sb.AppendLine("            background-color: rgba(46, 204, 113, 0.2);");
            sb.AppendLine("            color: var(--successful);");
            sb.AppendLine("            border: 1px solid rgba(46, 204, 113, 0.3);");
            sb.AppendLine("        }");
            sb.AppendLine("        .status-failed {");
            sb.AppendLine("            background-color: rgba(231, 76, 60, 0.2);");
            sb.AppendLine("            color: var(--error);");
            sb.AppendLine("            border: 1px solid rgba(231, 76, 60, 0.3);");
            sb.AppendLine("        }");
            sb.AppendLine("        .details-toggle {");
            sb.AppendLine("            background: none;");
            sb.AppendLine("            border: 1px solid var(--accent-blue);");
            sb.AppendLine("            color: var(--accent-blue);");
            sb.AppendLine("            border-radius: 4px;");
            sb.AppendLine("            padding: 5px 10px;");
            sb.AppendLine("            font-size: 12px;");
            sb.AppendLine("            cursor: pointer;");
            sb.AppendLine("            transition: all 0.2s ease;");
            sb.AppendLine("        }");
            sb.AppendLine("        .details-toggle:hover {");
            sb.AppendLine("            background-color: var(--accent-blue);");
            sb.AppendLine("            color: var(--background-dark);");
            sb.AppendLine("        }");
            sb.AppendLine("        .details-content {");
            sb.AppendLine("            display: none;");
            sb.AppendLine("            margin-top: 15px;");
            sb.AppendLine("            padding: 15px;");
            sb.AppendLine("            background-color: rgba(19, 24, 44, 0.5);");
            sb.AppendLine("            border-radius: 6px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .progress-bar-container {");
            sb.AppendLine("            height: 6px;");
            sb.AppendLine("            background-color: rgba(19, 24, 44, 0.5);");
            sb.AppendLine("            border-radius: 3px;");
            sb.AppendLine("            overflow: hidden;");
            sb.AppendLine("            margin-top: 5px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .progress-bar {");
            sb.AppendLine("            height: 100%;");
            sb.AppendLine("            background: linear-gradient(to right, var(--accent-blue), var(--accent-purple));");
            sb.AppendLine("            border-radius: 3px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .progress-success .progress-bar {");
            sb.AppendLine("            background: linear-gradient(to right, #27ae60, #2ecc71);");
            sb.AppendLine("        }");
            sb.AppendLine("        .progress-warning .progress-bar {");
            sb.AppendLine("            background: linear-gradient(to right, #e67e22, #f39c12);");
            sb.AppendLine("        }");
            sb.AppendLine("        .progress-error .progress-bar {");
            sb.AppendLine("            background: linear-gradient(to right, #c0392b, #e74c3c);");
            sb.AppendLine("        }");
            sb.AppendLine("        .error-list {");
            sb.AppendLine("            background-color: rgba(231, 76, 60, 0.1);");
            sb.AppendLine("            border-left: 3px solid var(--error);");
            sb.AppendLine("            border-radius: 3px;");
            sb.AppendLine("            padding: 15px;");
            sb.AppendLine("            margin-top: 20px;");
            sb.AppendLine("            max-height: 200px;");
            sb.AppendLine("            overflow-y: auto;");
            sb.AppendLine("        }");
            sb.AppendLine("        .error-list h3 {");
            sb.AppendLine("            color: var(--error);");
            sb.AppendLine("            margin-top: 0;");
            sb.AppendLine("            font-size: 16px;");
            sb.AppendLine("        }");
            sb.AppendLine("        .error-list ul {");
            sb.AppendLine("            padding-left: 20px;");
            sb.AppendLine("            margin: 10px 0 0;");
            sb.AppendLine("        }");
            sb.AppendLine("        .error-list li {");
            sb.AppendLine("            margin-bottom: 5px;");
            sb.AppendLine("            color: var(--text-bright);");
            sb.AppendLine("        }");
            sb.AppendLine("        footer {");
            sb.AppendLine("            text-align: center;");
            sb.AppendLine("            margin-top: 40px;");
            sb.AppendLine("            padding-top: 20px;");
            sb.AppendLine("            color: var(--text-muted);");
            sb.AppendLine("            font-size: 12px;");
            sb.AppendLine("            border-top: 1px solid rgba(52, 152, 219, 0.2);");
            sb.AppendLine("        }");
            sb.AppendLine("    </style>");
            sb.AppendLine("    <script>");
            sb.AppendLine("        function toggleDetails(appId) {");
            sb.AppendLine("            const content = document.getElementById(`details-${appId}`);");
            sb.AppendLine("            const button = document.getElementById(`toggle-${appId}`);");
            sb.AppendLine("            if (content.style.display === 'block') {");
            sb.AppendLine("                content.style.display = 'none';");
            sb.AppendLine("                button.textContent = 'Show Details';");
            sb.AppendLine("            } else {");
            sb.AppendLine("                content.style.display = 'block';");
            sb.AppendLine("                button.textContent = 'Hide Details';");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    </script>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("    <div class=\"container\">");
            sb.AppendLine("        <header>");
            sb.AppendLine("            <h1>DepotDumper Operation Report</h1>");
            sb.AppendLine($"            <div class=\"timestamp\">Generated on {DateTime.Now:MMMM d, yyyy} at {DateTime.Now:h:mm:ss tt}</div>"); // Corrected ordinal format removed for simplicity
            sb.AppendLine("        </header>");
            sb.AppendLine("        <div class=\"card\">");
            sb.AppendLine("            <div class=\"card-header\">");
            sb.AppendLine("                <div class=\"card-title\">Operation Summary</div>");
            sb.AppendLine($"                <div>Duration: {FormatTimeSpan(summary.Duration)}</div>");
            sb.AppendLine("            </div>");
            sb.AppendLine("            <div class=\"card-content\">");
            sb.AppendLine("                <div class=\"stat-grid\">");
            sb.AppendLine("                    <div class=\"stat-box\">");
            sb.AppendLine("                        <div class=\"stat-label\">TOTAL APPS</div>");
            sb.AppendLine($"                        <div class=\"stat-value\">{summary.TotalAppsProcessed}</div>");
            sb.AppendLine("                    </div>");
            string appSuccessClass = summary.FailedApps == 0 ? "success" : "";
            sb.AppendLine($"                    <div class=\"stat-box {appSuccessClass}\">");
            sb.AppendLine("                        <div class=\"stat-label\">SUCCESSFUL APPS</div>");
            sb.AppendLine($"                        <div class=\"stat-value success-value\">{summary.SuccessfulApps}</div>");
            if (summary.TotalAppsProcessed > 0)
            {
                int appPercentage = (int)Math.Round((double)summary.SuccessfulApps / summary.TotalAppsProcessed * 100);
                string progressClass = appPercentage >= 90 ? "progress-success" : (appPercentage >= 50 ? "progress-warning" : "progress-error");
                sb.AppendLine($"                        <div class=\"progress-bar-container {progressClass}\">");
                sb.AppendLine($"                            <div class=\"progress-bar\" style=\"width: {appPercentage}%\"></div>");
                sb.AppendLine("                        </div>");
            }
            sb.AppendLine("                    </div>");
            if (summary.FailedApps > 0)
            {
                sb.AppendLine("                    <div class=\"stat-box error\">");
                sb.AppendLine("                        <div class=\"stat-label\">FAILED APPS</div>");
                sb.AppendLine($"                        <div class=\"stat-value error-value\">{summary.FailedApps}</div>");
                sb.AppendLine("                    </div>");
            }
            sb.AppendLine("                    <div class=\"stat-box\">");
            sb.AppendLine("                        <div class=\"stat-label\">TOTAL DEPOTS</div>");
            sb.AppendLine($"                        <div class=\"stat-value\">{summary.TotalDepotsProcessed}</div>");
            sb.AppendLine("                    </div>");
            string depotSuccessClass = summary.FailedDepots == 0 ? "success" : "";
            sb.AppendLine($"                    <div class=\"stat-box {depotSuccessClass}\">");
            sb.AppendLine("                        <div class=\"stat-label\">SUCCESSFUL DEPOTS</div>");
            sb.AppendLine($"                        <div class=\"stat-value success-value\">{summary.SuccessfulDepots}</div>");
            if (summary.TotalDepotsProcessed > 0)
            {
                int depotPercentage = (int)Math.Round((double)summary.SuccessfulDepots / summary.TotalDepotsProcessed * 100);
                string progressClass = depotPercentage >= 90 ? "progress-success" : (depotPercentage >= 50 ? "progress-warning" : "progress-error");
                sb.AppendLine($"                        <div class=\"progress-bar-container {progressClass}\">");
                sb.AppendLine($"                            <div class=\"progress-bar\" style=\"width: {depotPercentage}%\"></div>");
                sb.AppendLine("                        </div>");
            }
            sb.AppendLine("                    </div>");
            if (summary.FailedDepots > 0)
            {
                sb.AppendLine("                    <div class=\"stat-box error\">");
                sb.AppendLine("                        <div class=\"stat-label\">FAILED DEPOTS</div>");
                sb.AppendLine($"                        <div class=\"stat-value error-value\">{summary.FailedDepots}</div>");
                sb.AppendLine("                    </div>");
            }
            sb.AppendLine("                    <div class=\"stat-box\">");
            sb.AppendLine("                        <div class=\"stat-label\">TOTAL MANIFESTS</div>");
            sb.AppendLine($"                        <div class=\"stat-value\">{summary.TotalManifestsProcessed}</div>");
            sb.AppendLine("                    </div>");
            sb.AppendLine("                    <div class=\"stat-box success\">");
            sb.AppendLine("                        <div class=\"stat-label\">NEW MANIFESTS</div>");
            sb.AppendLine($"                        <div class=\"stat-value success-value\">{summary.NewManifestsDownloaded}</div>");
            sb.AppendLine("                    </div>");
            sb.AppendLine("                    <div class=\"stat-box\">");
            sb.AppendLine("                        <div class=\"stat-label\">SKIPPED MANIFESTS</div>");
            sb.AppendLine($"                        <div class=\"stat-value\">{summary.ManifestsSkipped}</div>");
            sb.AppendLine("                    </div>");
            if (summary.FailedManifests > 0)
            {
                sb.AppendLine("                    <div class=\"stat-box error\">");
                sb.AppendLine("                        <div class=\"stat-label\">FAILED MANIFESTS</div>");
                sb.AppendLine($"                        <div class=\"stat-value error-value\">{summary.FailedManifests}</div>");
                sb.AppendLine("                    </div>");
            }
            sb.AppendLine("                </div>");
            sb.AppendLine("            </div>");
            sb.AppendLine("        </div>");
            sb.AppendLine("        <div class=\"card\">");
            sb.AppendLine("            <div class=\"card-header\">");
            sb.AppendLine("                <div class=\"card-title\">Processed Apps</div>");
            sb.AppendLine($"                <div>{summary.AppSummaries.Count} apps</div>");
            sb.AppendLine("            </div>");
            sb.AppendLine("            <div class=\"card-content\">");
            sb.AppendLine("                <table class=\"app-table\">");
            sb.AppendLine("                    <thead>");
            sb.AppendLine("                        <tr>");
            sb.AppendLine("                            <th>App ID</th>");
            sb.AppendLine("                            <th>Name</th>");
            sb.AppendLine("                            <th>Last Updated</th>"); // ADDED HEADER
            sb.AppendLine("                            <th>Depots</th>");
            sb.AppendLine("                            <th>Manifests</th>");
            sb.AppendLine("                            <th>Status</th>");
            sb.AppendLine("                            <th>Actions</th>");
            sb.AppendLine("                        </tr>");
            sb.AppendLine("                    </thead>");
            sb.AppendLine("                    <tbody>");
            foreach (var app in summary.AppSummaries)
            {
                string statusClass = app.ComputedSuccess ? "status-success" : "status-failed";
                string statusText = app.ComputedSuccess ? "Success" : "Failed";
                sb.AppendLine("                        <tr>");
                sb.AppendLine($"                            <td>{app.AppId}</td>");
                sb.AppendLine($"                            <td>{HtmlEncode(app.AppName)}</td>");
                sb.AppendLine($"                            <td>{app.LastUpdated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}</td>"); // ADDED DATA CELL
                sb.AppendLine($"                            <td>{app.ProcessedDepots}/{app.TotalDepots}</td>");
                sb.AppendLine($"                            <td>{app.NewManifests} new, {app.SkippedManifests} skipped</td>");
                sb.AppendLine($"                            <td><span class=\"app-status {statusClass}\">{statusText}</span></td>");
                sb.AppendLine($"                            <td><button id=\"toggle-{app.AppId}\" class=\"details-toggle\" onclick=\"toggleDetails({app.AppId})\">Show Details</button></td>");
                sb.AppendLine("                        </tr>");
                sb.AppendLine("                        <tr>");
                sb.AppendLine($"                            <td colspan=\"7\"><div id=\"details-{app.AppId}\" class=\"details-content\">"); // Adjusted colspan
                if (app.DepotSummaries.Count > 0)
                {
                    sb.AppendLine("                                <h3>Depot Details</h3>");
                    sb.AppendLine("                                <table class=\"app-table\">");
                    sb.AppendLine("                                    <thead>");
                    sb.AppendLine("                                        <tr>");
                    sb.AppendLine("                                            <th>Depot ID</th>");
                    sb.AppendLine("                                            <th>Manifests Found</th>");
                    sb.AppendLine("                                            <th>Downloaded</th>");
                    sb.AppendLine("                                            <th>Skipped</th>");
                    sb.AppendLine("                                            <th>Status</th>");
                    sb.AppendLine("                                        </tr>");
                    sb.AppendLine("                                    </thead>");
                    sb.AppendLine("                                    <tbody>");
                    foreach (var depot in app.DepotSummaries)
                    {
                        string depotStatusClass = depot.ComputedSuccess ? "status-success" : "status-failed";
                        string depotStatusText = depot.ComputedSuccess ? "Success" : "Failed";
                        sb.AppendLine("                                        <tr>");
                        sb.AppendLine($"                                            <td>{depot.DepotId}</td>");
                        sb.AppendLine($"                                            <td>{depot.ManifestsFound}</td>");
                        sb.AppendLine($"                                            <td>{depot.ManifestsDownloaded}</td>");
                        sb.AppendLine($"                                            <td>{depot.ManifestsSkipped}</td>");
                        sb.AppendLine($"                                            <td><span class=\"app-status {depotStatusClass}\">{depotStatusText}</span></td>");
                        sb.AppendLine("                                        </tr>");
                    }
                    sb.AppendLine("                                    </tbody>");
                    sb.AppendLine("                                </table>");
                }
                if (app.AppErrors.Count > 0)
                {
                    sb.AppendLine("                                <div class=\"error-list\">");
                    sb.AppendLine($"                                    <h3>Errors ({app.AppErrors.Count})</h3>");
                    sb.AppendLine("                                    <ul>");
                    foreach (var error in app.AppErrors)
                    {
                        sb.AppendLine($"                                        <li>{HtmlEncode(error)}</li>");
                    }
                    sb.AppendLine("                                    </ul>");
                    sb.AppendLine("                                </div>");
                }
                sb.AppendLine("                            </div></td>");
                sb.AppendLine("                        </tr>");
            }
            sb.AppendLine("                    </tbody>");
            sb.AppendLine("                </table>");
            sb.AppendLine("            </div>");
            sb.AppendLine("        </div>");
            if (summary.Errors.Count > 0)
            {
                sb.AppendLine("        <div class=\"card\">");
                sb.AppendLine("            <div class=\"card-header\">");
                sb.AppendLine("                <div class=\"card-title\">Error Summary</div>");
                sb.AppendLine($"                <div>{summary.Errors.Count} errors</div>");
                sb.AppendLine("            </div>");
                sb.AppendLine("            <div class=\"card-content\">");
                sb.AppendLine("                <div class=\"error-list\">");
                sb.AppendLine("                    <ul>");
                var errorGroups = summary.Errors
                    .GroupBy(error => GetErrorType(error))
                    .OrderByDescending(g => g.Count());
                foreach (var group in errorGroups)
                {
                    sb.AppendLine($"                        <li><strong>{group.Key}:</strong> {group.Count()} occurrences</li>");
                    // Show a few examples per group
                    foreach (var error in group.Take(3))
                    {
                        sb.AppendLine($"                        <li style=\"margin-left: 20px; font-size: 12px;\">{HtmlEncode(error)}</li>");
                    }
                    if (group.Count() > 3)
                    {
                        sb.AppendLine($"                        <li style=\"margin-left: 20px; font-size: 12px; color: var(--text-muted);\">...and {group.Count() - 3} more similar errors</li>");
                    }
                }
                sb.AppendLine("                    </ul>");
                sb.AppendLine("                </div>");
                sb.AppendLine("            </div>");
                sb.AppendLine("        </div>");
            }
            sb.AppendLine("        <footer>");
            sb.AppendLine("            <p>Generated by DepotDumper</p>");
            sb.AppendLine($"            <p>Start Time: {summary.StartTime:yyyy-MM-dd HH:mm:ss} | End Time: {summary.EndTime:yyyy-MM-dd HH:mm:ss} | Duration: {FormatTimeSpan(summary.Duration)}</p>");
            sb.AppendLine("        </footer>");
            sb.AppendLine("    </div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        // Helper to encode text for HTML display
        private static string HtmlEncode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            // Basic HTML encoding
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

        // Helper function to categorize errors based on keywords
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

        // Helper function to format TimeSpan nicely
        private static string FormatTimeSpan(TimeSpan span)
        {
            if (span.TotalDays >= 1)
                return $"{span.Days}d {span.Hours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalHours >= 1)
                return $"{span.Hours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalMinutes >= 1)
                return $"{span.Minutes}m {span.Seconds}s";
            return $"{span.Seconds}.{span.Milliseconds / 10}s"; // Show tenths of a second
        }
    }
}