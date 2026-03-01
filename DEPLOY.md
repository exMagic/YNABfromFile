# Deployment Instructions

## Building for Linux (Intel/AMD x64)

Build a self-contained single-file executable:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/linux-x64-single
```

This creates a single executable at `./publish/linux-x64-single/YNABfromFile` that includes the .NET runtime and all dependencies.

## Transferring to Linux Server

### Option 1: Using SCP

```bash
scp ./publish/linux-x64-single/YNABfromFile username@hostname:/path/to/destination/
```

### Option 2: Using SCP with appsettings.json

If you need the configuration file as well:

```bash
scp ./publish/linux-x64-single/YNABfromFile username@hostname:/path/to/destination/
scp ./appsettings.json username@hostname:/path/to/destination/
```

Replace:
- `username` - your SSH username
- `hostname` - server IP address or domain name
- `/path/to/destination/` - target directory on the server

## Setting Up on Linux

1. **Make the executable runnable:**

```bash
ssh username@hostname "chmod +x /path/to/destination/YNABfromFile"
```

2. **Test the application:**

```bash
ssh username@hostname "/path/to/destination/YNABfromFile"
```

## Setting Up as a Service (Optional)

If using the included service scripts:

1. **Transfer service scripts:**

```bash
scp ./service.sh username@hostname:/path/to/destination/
scp ./setup_autostart.sh username@hostname:/path/to/destination/
```

2. **Set up autostart:**

```bash
ssh username@hostname "cd /path/to/destination && chmod +x setup_autostart.sh && ./setup_autostart.sh"
```

## Example Deployment

```bash
# Build
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/linux-x64-single

# Deploy
scp ./publish/linux-x64-single/YNABfromFile user@192.168.1.100:/home/user/app/
scp ./appsettings.json user@192.168.1.100:/home/user/app/

# Configure
ssh user@192.168.1.100 "chmod +x /home/user/app/YNABfromFile"

# Run
ssh user@192.168.1.100 "/home/user/app/YNABfromFile"
```

## Notes

- The single-file executable is self-contained and doesn't require .NET to be installed on the Linux server
- Make sure to configure `appsettings.json` with your specific settings before deploying
- The executable size will be larger (~60-80 MB) because it includes the .NET runtime
