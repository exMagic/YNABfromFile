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
using System.Collections.Concurrent;
using CsvHelper;
using CsvHelper.Configuration;
using HtmlAgilityPack;
using System.Text.Json.Serialization;

class Program
{
    // Settings from config file
    private static AppSettings Settings { get; set; }
    private static readonly HttpClient httpClient = new HttpClient();
    
    // Thread-safe set of files currently being processed
    private static readonly ConcurrentDictionary<string, bool> FilesBeingProcessed = new ConcurrentDictionary<string, bool>();

    static void Main()
    {
        try
        {
            // Load settings from the JSON file
            LoadSettings();

            // Initialize HttpClient with YNAB API key
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Settings.YnabApiKey);
            
            // List to keep track of watchers so they stay in scope
            var watchers = new List<FileSystemWatcher>();
            
            // Handle multiple accounts (new format)
            if (Settings.Accounts != null && Settings.Accounts.Count > 0)
            {
                Console.WriteLine($"Found {Settings.Accounts.Count} accounts in settings");
                
                foreach (var account in Settings.Accounts)
                {
                    if (string.IsNullOrEmpty(account.YnabAccountId))
                    {
                        Console.WriteLine("Skipping account with missing YnabAccountId");
                        continue;
                    }
                    
                    if (string.IsNullOrEmpty(account.MonitoringFolderPath))
                    {
                        Console.WriteLine($"Skipping account {account.YnabAccountId} with missing MonitoringFolderPath");
                        continue;
                    }
                    
                    SetupMonitoring(account.MonitoringFolderPath, account.YnabAccountId, watchers);
                }
            }
            // Handle single account (backward compatibility with old format)
            else if (!string.IsNullOrEmpty(Settings.YnabAccountId) && !string.IsNullOrEmpty(Settings.MonitoringFolderPath))
            {
                Console.WriteLine("Using legacy single account settings");
                SetupMonitoring(Settings.MonitoringFolderPath, Settings.YnabAccountId, watchers);
            }
            else
            {
                throw new Exception("No valid account configurations found in settings file");
            }
            
            Console.WriteLine("Press [Enter] to exit.");
            Console.ReadLine(); // Keep the application running
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting application: {ex.Message}");
            Console.WriteLine("Press [Enter] to exit.");
            Console.ReadLine();
        }
    }

    private static void SetupMonitoring(string folderPath, string accountId, List<FileSystemWatcher> watchers)
    {
        Console.WriteLine($"Setting up monitoring for account {accountId} in folder: {folderPath}");
        
        // Check if folder exists, if not create it
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            Console.WriteLine($"Created folder: {folderPath}");
        }

        // Create Archives subfolder if it doesn't exist
        string archivesPath = Path.Combine(folderPath, "Archives");
        if (!Directory.Exists(archivesPath))
        {
            Directory.CreateDirectory(archivesPath);
            Console.WriteLine($"Created archives folder: {archivesPath}");
        }

        // Create a file system watcher for this folder
        FileSystemWatcher watcher = new FileSystemWatcher(folderPath);
        watcher.Filter = "*.html"; // Monitor only HTML files
        
        // Create a state object to pass to the event handler
        var watcherState = new WatcherState { AccountId = accountId, FolderPath = folderPath };
        
        // Using the overload that accepts object state
        watcher.Created += (sender, e) => OnNewFileDetected(sender, e, watcherState);
        watcher.EnableRaisingEvents = true; // Activate listening
        
        // Add to our collection so it doesn't get garbage collected
        watchers.Add(watcher);
        
        Console.WriteLine($"Started monitoring folder: {folderPath} for account: {accountId}");
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
        
        // Validate required global settings
        if (string.IsNullOrEmpty(Settings.YnabApiKey))
            throw new Exception("YNAB API Key is missing in settings file");
        
        if (string.IsNullOrEmpty(Settings.YnabBudgetId))
            throw new Exception("YNAB Budget ID is missing in settings file");
        
        // Check if we have accounts configured in the new format
        bool hasNewFormatAccounts = Settings.Accounts != null && Settings.Accounts.Count > 0;
        
        // Check if we have an account configured in the old format
        bool hasOldFormatAccount = !string.IsNullOrEmpty(Settings.YnabAccountId) && 
                                   !string.IsNullOrEmpty(Settings.MonitoringFolderPath);
        
        // Ensure we have at least one valid account configuration
        if (!hasNewFormatAccounts && !hasOldFormatAccount)
        {
            throw new Exception("No valid account configurations found in settings file. " + 
                                "Either provide YnabAccountId and MonitoringFolderPath, " +
                                "or configure Accounts array with at least one account.");
        }
        
        Console.WriteLine("Settings loaded successfully");
    }

    private static void OnNewFileDetected(object sender, FileSystemEventArgs e, WatcherState watcherState)
    {
        // Ignore files starting with "modified_"
        string fileName = Path.GetFileName(e.FullPath);
        if (fileName.StartsWith("modified_"))
        {
            Console.WriteLine($"Ignored file: {e.FullPath}");
            return;
        }

        // Skip if the file is already being processed
        if (!FilesBeingProcessed.TryAdd(e.FullPath, true))
        {
            Console.WriteLine($"File already being processed: {e.FullPath}");
            return;
        }

        try
        {
            Console.WriteLine($"New file detected: {e.FullPath} for account {watcherState.AccountId}");

            // Check if the file exists before processing
            if (!File.Exists(e.FullPath))
            {
                Console.WriteLine($"File no longer exists: {e.FullPath}");
                return;
            }

            // Generate output path for the modified file
            string outputFilePath = Path.Combine(
                Path.GetDirectoryName(e.FullPath), // Same path as the input file
                "modified_" + Path.GetFileNameWithoutExtension(fileName) + ".csv" // Add prefix and change extension
            );

            bool importSuccess = ProcessHtml(e.FullPath, outputFilePath, watcherState.AccountId);
            Console.WriteLine($"Processed file saved as: {outputFilePath}");

            // If import was successful, move files to archives
            if (importSuccess)
            {
                MoveToArchives(e.FullPath, outputFilePath, watcherState.FolderPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing file: {ex.Message}");
        }
        finally
        {
            // Remove the file from our processing list regardless of success or failure
            FilesBeingProcessed.TryRemove(e.FullPath, out _);
        }
    }

    private static void MoveToArchives(string originalFilePath, string modifiedFilePath, string folderPath)
    {
        try
        {
            string archivesPath = Path.Combine(folderPath, "Archives");
            
            // Create archive path if it doesn't exist
            if (!Directory.Exists(archivesPath))
            {
                Directory.CreateDirectory(archivesPath);
                Console.WriteLine($"Created archives folder: {archivesPath}");
            }

            // Create a unique subfolder using date+time with seconds
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string archiveSubfolderPath = Path.Combine(archivesPath, timestamp);
            
            // Create the unique subfolder
            Directory.CreateDirectory(archiveSubfolderPath);
            Console.WriteLine($"Created archive subfolder: {archiveSubfolderPath}");

            // Get original filenames without changing them
            string originalFileName = Path.GetFileName(originalFilePath);
            string modifiedFileName = Path.GetFileName(modifiedFilePath);
            
            // Define destination paths in the new subfolder with original filenames
            string archivedOriginalPath = Path.Combine(archiveSubfolderPath, originalFileName);
            string archivedModifiedPath = Path.Combine(archiveSubfolderPath, modifiedFileName);

            // Move files to archive subfolder
            File.Move(originalFilePath, archivedOriginalPath, true);
            File.Move(modifiedFilePath, archivedModifiedPath, true);

            Console.WriteLine($"Moved original file to: {archivedOriginalPath}");
            Console.WriteLine($"Moved modified file to: {archivedModifiedPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error moving files to archives: {ex.Message}");
        }
    }

    private static bool ProcessHtml(string inputFile, string outputFile, string accountId)
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
        bool importSuccess = false;

        using (var writer = new StreamWriter(outputFile))
        using (var csvWriter = new CsvWriter(writer, config))
        {
            // Write the header for the output CSV
            csvWriter.WriteField("Date");
            csvWriter.WriteField("Payee");
            csvWriter.WriteField("Memo");
            csvWriter.WriteField("Outflow");
            csvWriter.WriteField("Inflow");
            csvWriter.WriteField("Balance");
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
                            string balanceText = cells[4].InnerText.Trim().Replace(" ", "").Replace(',', '.');
                            
                            Console.WriteLine($"Processing row - Date: {originalDate}, Original Payee: {payee}, Amount: {amountText}, Balance: {balanceText}");
                            
                            decimal amount = decimal.Parse(amountText, CultureInfo.InvariantCulture);
                            decimal balance = decimal.Parse(balanceText, CultureInfo.InvariantCulture);
                            decimal outflow = amount < 0 ? Math.Abs(amount) : 0;
                            decimal inflow = amount > 0 ? amount : 0;

                            // Split the payee text on <br> and parse the parts
                            var payeeParts = payee.Split(new[] { "<br>" }, StringSplitOptions.None);
                            string transactionDate = "";
                            string payeeName = payeeParts.Length > 1 ? payeeParts[1].Trim() : payee;

                            // Extract DATA TRANSAKCJI from any part of the payee text
                            const string transactionDatePrefix = "DATA TRANSAKCJI:";
                            foreach (var part in payeeParts)
                            {
                                int transactionDateIndex = part.IndexOf(transactionDatePrefix, StringComparison.OrdinalIgnoreCase);
                                if (transactionDateIndex >= 0)
                                {
                                    int startIndex = transactionDateIndex + transactionDatePrefix.Length;
                                    transactionDate = part.Substring(startIndex).Trim();
                                    Console.WriteLine($"  Found transaction date in payee: {transactionDate}");
                                    break;
                                }
                            }

                            // Use DATA TRANSAKCJI if available, otherwise fall back to the original date
                            string dateToUse = !string.IsNullOrEmpty(transactionDate) ? transactionDate : originalDate;
                            Console.WriteLine($"  Using date: {dateToUse}");

                            // Remove "/" and everything after it from payee
                            int slashIndex = payeeName.IndexOf('/');
                            if (slashIndex >= 0)
                            {
                                payeeName = payeeName.Substring(0, slashIndex).Trim();
                            }

                            Console.WriteLine($"  Processed data - Date: {dateToUse}, Payee: {payeeName}, Amount: {amount}, Balance: {balance}");

                            // Write to CSV
                            csvWriter.WriteField(dateToUse);
                            csvWriter.WriteField(payeeName);
                            csvWriter.WriteField(memo);
                            csvWriter.WriteField(outflow);
                            csvWriter.WriteField(inflow);
                            csvWriter.WriteField(balance);
                            csvWriter.NextRecord();
                            
                            // Try to parse the date to YNAB format (ISO date)
                            if (DateTime.TryParse(dateToUse, out DateTime parsedDate))
                            {
                                // Create a shorter import_id that's under 36 characters
                                // Format: short prefix + date + amount hash + balance hash + payee hash
                                string dateStr = parsedDate.ToString("yyyyMMdd");
                                int amountHash = Math.Abs(amount.GetHashCode() % 100); // Get a 2-digit hash of the amount
                                int balanceHash = Math.Abs(balance.GetHashCode() % 100); // Get a 2-digit hash of the balance
                                int payeeHash = Math.Abs(payeeName.GetHashCode() % 1000); // Get a 3-digit hash of the payee
                                
                                var transaction = new YnabTransaction
                                {
                                    AccountId = accountId,
                                    Date = parsedDate.ToString("yyyy-MM-dd"),
                                    Amount = (int)(amount * 1000), // YNAB requires amount in milliunits (multiply by 1000)
                                    PayeeName = payeeName,
                                    Memo = "", // Remove memo content as requested
                                    Cleared = "cleared",
                                    ImportId = $"I:{dateStr}:{amountHash}{balanceHash}{payeeHash}" // Unique ID with payee hash, no timestamp
                                };
                                ynabTransactions.Add(transaction);
                                parsedTransactionsCount++;
                                Console.WriteLine($"  Successfully created YNAB transaction object with date {transaction.Date}");
                            }
                            else
                            {
                                Console.WriteLine($"  Warning: Could not parse date '{dateToUse}' for YNAB API");
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
                    importSuccess = true;
                }
                if (importResult.DuplicateImportIds?.Count > 0)
                {
                    Console.WriteLine($"Duplicate transactions found: {importResult.DuplicateImportIds.Count}");
                    // Still consider this a success since duplicates are expected sometimes
                    importSuccess = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending transactions to YNAB: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                }
                importSuccess = false;
            }
        }
        else
        {
            Console.WriteLine("No transactions were created to send to YNAB");
            importSuccess = false;
        }

        return importSuccess;
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
        string requestUrl = $"{Settings.YnabApiUrl}/budgets/{Settings.YnabBudgetId}/transactions";
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
    public string YnabApiUrl { get; set; } = "https://api.ynab.com/v1";
    public List<AccountSettings> Accounts { get; set; } = new List<AccountSettings>();

    // For backward compatibility with the old settings format
    public string YnabAccountId { get; set; }
    public string MonitoringFolderPath { get; set; }
}

class AccountSettings 
{
    public string YnabAccountId { get; set; }
    public string MonitoringFolderPath { get; set; }
}

// YNAB API models
class YnabTransaction
{
    [JsonPropertyName("account_id")]
    public string AccountId { get; set; }
    public string Date { get; set; }
    public int Amount { get; set; }
    [JsonPropertyName("payee_name")]
    public string PayeeName { get; set; }
    public string Memo { get; set; }
    public string Cleared { get; set; }
    [JsonPropertyName("import_id")]
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
    [JsonPropertyName("transaction_ids")]
    public List<string> TransactionIds { get; set; } = new List<string>();
    [JsonPropertyName("duplicate_import_ids")]
    public List<string> DuplicateImportIds { get; set; } = new List<string>();
    // The /transactions endpoint does not return TransactionsImported, so we calculate it
    public int TransactionsImported => TransactionIds?.Count ?? 0;
}

class WatcherState
{
    public string AccountId { get; set; }
    public string FolderPath { get; set; }
}