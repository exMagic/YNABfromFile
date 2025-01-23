using System;
using System.Globalization;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;
using HtmlAgilityPack;

class Program
{
    static void Main()
    {
        string folderPath = @"C:\Users\Maciek\Documents\csvBank";
        FileSystemWatcher watcher = new FileSystemWatcher(folderPath);

        watcher.Filter = "*.html"; // Monitor only HTML files
        watcher.Created += OnNewFileDetected; // Subscribe to the event for new files

        watcher.EnableRaisingEvents = true; // Activate listening

        Console.WriteLine($"Monitoring folder: {folderPath}. Press [Enter] to exit.");
        Console.ReadLine(); // Keep the application running
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
                foreach (var row in rows)
                {
                    var cells = row.SelectNodes("td");
                    if (cells != null && cells.Count >= 5)
                    {
                        try
                        {
                            string date = cells[0].InnerText.Trim();
                            string payee = cells[2].InnerHtml.Trim();
                            string memo = "";
                            string amountText = cells[3].InnerText.Trim().Replace(" ", "").Replace(',', '.');
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
                                date = transactionDate;
                            }

                            // Remove "/" and everything after it from payee
                            int slashIndex = payee.IndexOf('/');
                            if (slashIndex >= 0)
                            {
                                payee = payee.Substring(0, slashIndex).Trim();
                            }

                            csvWriter.WriteField(date);
                            csvWriter.WriteField(payee);
                            csvWriter.WriteField(memo);
                            csvWriter.WriteField(outflow);
                            csvWriter.WriteField(inflow);
                            csvWriter.NextRecord();
                        }
                        catch (FormatException ex)
                        {

                            Console.WriteLine($"Format error: {ex.Message}");
                            // Optionally, log the format error or take other actions
                        }
                    }
                }
            }
        }
    }
}