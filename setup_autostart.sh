#!/bin/zsh

# Create LaunchAgents directory if it doesn't exist
mkdir -p ~/Library/LaunchAgents
mkdir -p ~/Library/Logs

# Create the Launch Agent plist file
cat > ~/Library/LaunchAgents/com.ynabfromfile.plist << 'EOL'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.ynabfromfile</string>
    <key>ProgramArguments</key>
    <array>
        <string>/usr/local/share/dotnet/dotnet</string>
        <string>/Users/maciejanuszkiewicz/Repo/YNABfromFile/bin/Debug/net8.0/YNABfromFile.dll</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <key>StandardOutPath</key>
    <string>/Users/maciejanuszkiewicz/Library/Logs/YNABfromFile.log</string>
    <key>StandardErrorPath</key>
    <string>/Users/maciejanuszkiewicz/Library/Logs/YNABfromFile.error.log</string>
    <key>WorkingDirectory</key>
    <string>/Users/maciejanuszkiewicz/Repo/YNABfromFile/bin/Debug/net8.0</string>
</dict>
</plist>
EOL

# Set the correct permissions for the plist file
chmod 644 ~/Library/LaunchAgents/com.ynabfromfile.plist

# Unload the Launch Agent if it's already loaded (will fail if it's not, that's OK)
launchctl bootout gui/$UID ~/Library/LaunchAgents/com.ynabfromfile.plist 2>/dev/null || true

# Load the Launch Agent
launchctl bootstrap gui/$UID ~/Library/LaunchAgents/com.ynabfromfile.plist

echo "Setup complete! The YNABfromFile service has been configured to start automatically."
echo "You can find the logs in:"
echo "  ~/Library/Logs/YNABfromFile.log"
echo "  ~/Library/Logs/YNABfromFile.error.log"
echo ""
echo "To manually control the service:"
echo "  Start: launchctl bootstrap gui/$UID ~/Library/LaunchAgents/com.ynabfromfile.plist"
echo "  Stop:  launchctl bootout gui/$UID ~/Library/LaunchAgents/com.ynabfromfile.plist"
echo "  Check status: launchctl list | grep ynabfromfile"
