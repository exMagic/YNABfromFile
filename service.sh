#!/bin/zsh

# Function to show usage
show_usage() {
    echo "YNABfromFile Service Control Script"
    echo "Usage: $0 [command]"
    echo ""
    echo "Commands:"
    echo "  status    - Check if the service is running"
    echo "  start     - Start the service"
    echo "  stop      - Stop the service"
    echo "  restart   - Restart the service"
    echo "  reload    - Copy new config and restart service"
    echo "  logs      - Show service logs (use Ctrl+C to exit)"
    echo "  errors    - Show error logs (use Ctrl+C to exit)"
}

# Check if command is provided
if [ $# -eq 0 ]; then
    show_usage
    exit 1
fi

# Service paths
PLIST_PATH="$HOME/Library/LaunchAgents/com.ynabfromfile.plist"
CONFIG_SOURCE="$HOME/Repo/YNABfromFile/appsettings.json"
CONFIG_TARGET="$HOME/Repo/YNABfromFile/bin/Debug/net8.0/appsettings.json"

# Function to check if service is loaded
is_service_loaded() {
    launchctl list | grep -q "com.ynabfromfile"
    return $?
}

case "$1" in
    "status")
        if is_service_loaded; then
            echo "Service is running"
            launchctl list | grep "com.ynabfromfile"
        else
            echo "Service is not running"
        fi
        ;;
    
    "start")
        echo "Starting service..."
        launchctl bootstrap gui/$UID "$PLIST_PATH"
        sleep 1
        if is_service_loaded; then
            echo "Service started successfully"
        else
            echo "Failed to start service"
            exit 1
        fi
        ;;
    
    "stop")
        echo "Stopping service..."
        launchctl bootout gui/$UID "$PLIST_PATH" 2>/dev/null || true
        sleep 1
        if ! is_service_loaded; then
            echo "Service stopped successfully"
        else
            echo "Failed to stop service"
            exit 1
        fi
        ;;
    
    "restart")
        echo "Restarting service..."
        $0 stop
        sleep 1
        $0 start
        ;;
    
    "reload")
        echo "Reloading configuration..."
        echo "Copying new config file..."
        cp "$CONFIG_SOURCE" "$CONFIG_TARGET"
        echo "Restarting service..."
        $0 restart
        ;;
    
    "logs")
        echo "Showing service logs (Ctrl+C to exit)..."
        tail -f ~/Library/Logs/YNABfromFile.log
        ;;
    
    "errors")
        echo "Showing error logs (Ctrl+C to exit)..."
        tail -f ~/Library/Logs/YNABfromFile.error.log
        ;;
    
    *)
        show_usage
        exit 1
        ;;
esac
