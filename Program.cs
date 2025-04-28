using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using HtmlAgilityPack;

class Program
{
    // Settings from config file
    private static AppSettings Settings { get; set; }
    private static readonly HttpClient httpClient = new HttpClient();

    static void Main()
    {
        try
        {
            // Load settings from the JSON file
            LoadSettings();

            // Initialize HttpClient with YNAB API key
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Settings.YnabApiKey);
            
            string folderPath = Settings.MonitoringFolderPath;
            
            // Check if folder exists, if not create it
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
                Console.WriteLine($"Created folder: {folderPath}");
            }

            FileSystemWatcher watcher = new FileSystemWatcher(folderPath);
            watcher.Filter = "*.html"; // Monitor only HTML files
            watcher.Created += OnNewFileDetected; // Subscribe to the event for new files
            watcher.EnableRaisingEvents = true; // Activate listening

            Console.WriteLine($"Monitoring folder: {folderPath}. Press [Enter] to exit.");
            Console.ReadLine(); // Keep the application running
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting application: {ex.Message}");
            Console.WriteLine("Press [Enter] to exit.");
            Console.ReadLine();
        }
    }

    private static void LoadSettings()
    {
        string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        
        // Check if settings file exists in the execution directory
        if (!File.Exists(settingsPath))
        {
            // Try to find it in the current directory or parent directories
            string currentDir = Directory.GetCurrentDirectory();
            string potentialPath = Path.Combine(currentDir, "appsettings.json");
            
            if (File.Exists(potentialPath))
            {
                settingsPath = potentialPath;
            }
            else
            {
                // Try one level up (project directory when running from bin/Debug)
                string parentDir = Directory.GetParent(currentDir)?.FullName;
                if (parentDir != null)
                {
                    potentialPath = Path.Combine(parentDir, "appsettings.json");
                    if (File.Exists(potentialPath))
                    {
                        settingsPath = potentialPath;
                    }
                    else
                    {
                        // Try two levels up
                        parentDir = Directory.GetParent(parentDir)?.FullName;
                        if (parentDir != null)
                        {
                            potentialPath = Path.Combine(parentDir, "appsettings.json");
                            if (File.Exists(potentialPath))
                            {
                                settingsPath = potentialPath;
                            }
                        }
                    }
                }
            }
        }
        
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException($"Settings file not found at {settingsPath}. Please create an appsettings.json file.");
        }
        
        string json = File.ReadAllText(settingsPath);
        Console.WriteLine($"Loaded settings from: {settingsPath}");
        
        Settings = JsonSerializer.Deserialize<AppSettings>(json, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        // Validate required settings
        if (string.IsNullOrEmpty(Settings.YnabApiKey))
            throw new Exception("YNAB API Key is missing in settings file");
        
        if (string.IsNullOrEmpty(Settings.YnabBudgetId))
            throw new Exception("YNAB Budget ID is missing in settings file");
            
        if (string.IsNullOrEmpty(Settings.YnabAccountId))
            throw new Exception("YNAB Account ID is missing in settings file");
            
        if (string.IsNullOrEmpty(Settings.MonitoringFolderPath))
            throw new Exception("Monitoring folder path is missing in settings file");
            
        Console.WriteLine("Settings loaded successfully");
    }

    private static void OnNewFileDetected(object sender, FileSystemEventArgs e)
    {
        // Ignore files starting with "modified_"
        string fileName = Path.GetFileName(e.FullPath);
        if (fileName.StartsWith("modified_"))
        {
            Console.WriteLine($"Ignored file: {e.FullPath}");
            return;
        }

        Console.WriteLine($"New file detected: {e.FullPath}");

        try
        {
            // Generate output path for the modified file
            string outputFilePath = Path.Combine(
                Path.GetDirectoryName(e.FullPath), // Same path as the input file
                "modified_" + Path.GetFileNameWithoutExtension(fileName) + ".csv" // Add prefix and change extension
            );

            ProcessHtml(e.FullPath, outputFilePath);
            Console.WriteLine($"Processed file saved as: {outputFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing file: {ex.Message}");
        }
    }

    private static void ProcessHtml(string inputFile, string outputFile)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ";", // Set the delimiter to semicolon
            BadDataFound = null, // Ignore bad data
        };

        var htmlDoc = new HtmlDocument();
        htmlDoc.Load(inputFile);
        
        // List to store all transactions for the YNAB API
        var ynabTransactions = new List<YnabTransaction>();
        int totalRowsFound = 0;
        int parsedTransactionsCount = 0;

        // Force the target year to be 2024 instead of relying on system clock
        // This ensures dates from 2025 will be adjusted to 2024
        int targetYear = 2024;
        Console.WriteLine($"Target year for date adjustments: {targetYear}");

        using (var writer = new StreamWriter(outputFile))
        using (var csvWriter = new CsvWriter(writer, config))
        {
            // Write the header for the output CSV
            csvWriter.WriteField("Date");
            csvWriter.WriteField("Payee");
            csvWriter.WriteField("Memo");
            csvWriter.WriteField("Outflow");
            csvWriter.WriteField("Inflow");
            csvWriter.NextRecord();

            // Extract data from the HTML
            var rows = htmlDoc.DocumentNode.SelectNodes("//table[@border='1']//tr[not(@class='head')]");
            if (rows != null)
            {
                totalRowsFound = rows.Count;
                Console.WriteLine($"Found {totalRowsFound} rows in HTML table");

                foreach (var row in rows)
                {
                    var cells = row.SelectNodes("td");
                    if (cells != null && cells.Count >= 5)
                    {
                        try
                        {
                            string originalDate = cells[0].InnerText.Trim();
                            string payee = cells[2].InnerHtml.Trim();
                            string memo = "";
                            string amountText = cells[3].InnerText.Trim().Replace(" ", "").Replace(',', '.');
                            
                            Console.WriteLine($"Processing row - Date: {originalDate}, Original Payee: {payee}, Amount: {amountText}");
                            
                            decimal amount = decimal.Parse(amountText, CultureInfo.InvariantCulture);
                            decimal outflow = amount < 0 ? Math.Abs(amount) : 0;
                            decimal inflow = amount > 0 ? amount : 0;

                            // Split the payee text on <br> and take the part after the first <br>
                            var payeeParts = payee.Split(new[] { "<br>" }, StringSplitOptions.None);
                            if (payeeParts.Length > 1)
                            {
                                payee = payeeParts[1].Trim();
                            }

                            // Check if payee contains "DATA TRANSAKCJI:" and extract the date
                            const string transactionDatePrefix = "DATA TRANSAKCJI:";
                            int transactionDateIndex = payee.IndexOf(transactionDatePrefix, StringComparison.OrdinalIgnoreCase);
                            if (transactionDateIndex >= 0)
                            {
                                int startIndex = transactionDateIndex + transactionDatePrefix.Length;
                                string transactionDate = payee.Substring(startIndex).Trim();
                                originalDate = transactionDate;
                                Console.WriteLine($"  Found transaction date in payee: {originalDate}");
                            }

                            // Remove "/" and everything after it from payee
                            int slashIndex = payee.IndexOf('/');
                            if (slashIndex >= 0)
                            {
                                payee = payee.Substring(0, slashIndex).Trim();
                            }

                            // Handle future dates by replacing the year with the target year
                            // Use regex to match date format like 2025-04-24
                            string adjustedDate = originalDate;
                            var dateMatch = Regex.Match(originalDate, @"(\d{4})-(\d{2})-(\d{2})");
                            if (dateMatch.Success)
                            {
                                int year = int.Parse(dateMatch.Groups[1].Value);
                                string month = dateMatch.Groups[2].Value;
                                string day = dateMatch.Groups[3].Value;
                                
                                // Always adjust the year to the target year
                                if (year != targetYear)
                                {
                                    adjustedDate = $"{targetYear}-{month}-{day}";
                                    Console.WriteLine($"  Adjusted date {originalDate} to {adjustedDate}");
                                }
                            }

                            Console.WriteLine($"  Processed data - Date: {adjustedDate}, Payee: {payee}, Amount: {amount}");

                            // Write to CSV
                            csvWriter.WriteField(adjustedDate);
                            csvWriter.WriteField(payee);
                            csvWriter.WriteField(memo);
                            csvWriter.WriteField(outflow);
                            csvWriter.WriteField(inflow);
                            csvWriter.NextRecord();
                            
                            // Try to parse the date to YNAB format (ISO date)
                            if (DateTime.TryParse(adjustedDate, out DateTime parsedDate))
                            {
                                // Create YNAB transaction and add to the list
                                var transaction = new YnabTransaction
                                {
                                    AccountId = Settings.YnabAccountId,
                                    Date = parsedDate.ToString("yyyy-MM-dd"),
                                    Amount = (int)(amount * 1000), // YNAB requires amount in milliunits (multiply by 1000)
                                    PayeeName = payee,
                                    Memo = memo,
                                    Cleared = "cleared",
                                    ImportId = $"IMPORT:{parsedDate:yyyy-MM-dd}:{Math.Abs(amount)}:{payee}" // Create a unique import ID
                                };
                                
                                ynabTransactions.Add(transaction);
                                parsedTransactionsCount++;
                                Console.WriteLine($"  Successfully created YNAB transaction object with date {transaction.Date}");
                            }
                            else
                            {
                                Console.WriteLine($"  Warning: Could not parse date '{adjustedDate}' for YNAB API");
                            }
                        }
                        catch (FormatException ex)
                        {
                            Console.WriteLine($"Format error while processing row: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Unexpected error while processing row: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Skipping row - not enough cells or null cells. Cell count: {cells?.Count ?? 0}");
                    }
                }
            }
            else
            {
                Console.WriteLine("No matching rows found in HTML document. Check your HTML structure and XPath selector.");
                // Let's try to print some info about the HTML structure
                var tables = htmlDoc.DocumentNode.SelectNodes("//table");
                Console.WriteLine($"Total tables found in HTML: {tables?.Count ?? 0}");
                if (tables != null)
                {
                    int tableIndex = 0;
                    foreach (var table in tables)
                    {
                        Console.WriteLine($"Table {tableIndex++} attributes: {table.GetAttributeValue("border", "none")}");
                        var tableRows = table.SelectNodes(".//tr");
                        Console.WriteLine($"  Rows in this table: {tableRows?.Count ?? 0}");
                    }
                }
            }
        }
        
        Console.WriteLine($"Summary: Found {totalRowsFound} rows, created {parsedTransactionsCount} YNAB transactions");
        
        // If we have transactions, send them to YNAB
        if (ynabTransactions.Count > 0)
        {
            try
            {
                Console.WriteLine($"Sending {ynabTransactions.Count} transactions to YNAB API...");
                // Log the first transaction as a sample
                if (ynabTransactions.Count > 0)
                {
                    var sample = ynabTransactions[0];
                    Console.WriteLine($"Sample transaction - Date: {sample.Date}, Payee: {sample.PayeeName}, Amount: {sample.Amount} milliunits");
                }
                
                // Call YNAB API to import transactions
                var importResult = SendTransactionsToYnab(ynabTransactions).GetAwaiter().GetResult();
                Console.WriteLine($"Successfully sent {importResult.TransactionsImported} transactions to YNAB");
                if (importResult.TransactionIds?.Count > 0)
                {
                    Console.WriteLine($"Imported transaction IDs: {string.Join(", ", importResult.TransactionIds)}");
                }
                if (importResult.DuplicateImportIds?.Count > 0)
                {
                    Console.WriteLine($"Duplicate transactions found: {importResult.DuplicateImportIds.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending transactions to YNAB: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }
            }
        }
        else
        {
            Console.WriteLine("No transactions were created to send to YNAB");
        }
    }
    
    private static async Task<YnabImportResponse> SendTransactionsToYnab(List<YnabTransaction> transactions)
    {
        // Create the request payload
        var payload = new YnabImportRequest
        {
            Transactions = transactions
        };
        
        // Serialize payload to JSON
        var jsonContent = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // Use camelCase for JSON properties
            WriteIndented = true
        });
        
        Console.WriteLine($"API request payload: {jsonContent}");
        
        // Prepare the HTTP content
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        
        // Send the request to YNAB API
        string requestUrl = $"{Settings.YnabApiUrl}/budgets/{Settings.YnabBudgetId}/transactions/import";
        Console.WriteLine($"Sending request to: {requestUrl}");
        
        var response = await httpClient.PostAsync(requestUrl, content);
        
        // Check if the request was successful
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"API response error: Status code {response.StatusCode}");
            Console.WriteLine($"API response body: {errorContent}");
            throw new Exception($"YNAB API error: {response.StatusCode} - {errorContent}");
        }
        
        // Parse the response
        var responseContent = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"API response: {responseContent}");
        
        var responseObject = JsonSerializer.Deserialize<YnabApiResponse>(responseContent, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        return responseObject.Data;
    }
}

// Settings model
class AppSettings
{
    public string YnabApiKey { get; set; }
    public string YnabBudgetId { get; set; }
    public string YnabAccountId { get; set; }
    public string MonitoringFolderPath { get; set; }
    public string YnabApiUrl { get; set; } = "https://api.ynab.com/v1";
}

// YNAB API models
class YnabTransaction
{
    public string AccountId { get; set; }
    public string Date { get; set; }
    public int Amount { get; set; }
    public string PayeeName { get; set; }
    public string Memo { get; set; }
    public string Cleared { get; set; }
    public string ImportId { get; set; }
}

class YnabImportRequest
{
    public List<YnabTransaction> Transactions { get; set; }
}

class YnabApiResponse
{
    public YnabImportResponse Data { get; set; }
}

class YnabImportResponse
{
    public int TransactionsImported { get; set; }
    public List<string> TransactionIds { get; set; } = new List<string>();
    public List<string> DuplicateImportIds { get; set; } = new List<string>();
}