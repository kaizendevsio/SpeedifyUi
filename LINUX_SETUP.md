# Linux Setup Guide for XNetwork Web Application

This guide explains how to configure your Linux system to allow the XNetwork web application to perform administrative operations (service restarts, server reboot) without requiring interactive password input.

## Problem Overview

The web application needs to execute privileged commands like:
- `sudo systemctl restart speedify` - Restart the Speedify service
- `sudo reboot` - Reboot the server

By default, these commands require root privileges and password authentication. The application **uses sudo** to execute these commands, but web applications cannot provide an interactive terminal for password input.

**Solution:** Configure passwordless sudo for specific commands. This allows the application to run administrative commands using sudo WITHOUT requiring password prompts, while maintaining security by limiting which commands can be executed without a password.

## Solution Options

### Option 1: Passwordless Sudo (Recommended)

This approach allows a specific user to run specific commands with sudo WITHOUT entering a password, while maintaining security by limiting which commands can be executed passwordlessly.

**How it works:** The application uses `sudo` for privileged commands, but the passwordless sudo configuration prevents the password prompt from appearing.

#### Step 1: Create a Dedicated User (Optional but Recommended)

Create a dedicated user to run the application:

```bash
sudo useradd -r -s /bin/bash -m -d /opt/xnetwork speedify-web
```

#### Step 2: Configure Passwordless Sudo

Create a sudoers configuration file:

```bash
sudo visudo -f /etc/sudoers.d/xnetwork-web
```

Add the following content (replace `speedify-web` with your actual username if different):

```
# Allow speedify-web user to restart Speedify service without password
speedify-web ALL=(ALL) NOPASSWD: /bin/systemctl restart speedify
speedify-web ALL=(ALL) NOPASSWD: /usr/bin/systemctl restart speedify
speedify-web ALL=(ALL) NOPASSWD: /sbin/service speedify restart
speedify-web ALL=(ALL) NOPASSWD: /usr/sbin/service speedify restart

# Allow speedify-web user to reboot the server without password
speedify-web ALL=(ALL) NOPASSWD: /sbin/reboot
speedify-web ALL=(ALL) NOPASSWD: /usr/sbin/reboot

# Allow speedify-web user to manage network routing for bypass mode
speedify-web ALL=(ALL) NOPASSWD: /sbin/ip route add default *
speedify-web ALL=(ALL) NOPASSWD: /sbin/ip route del default *
speedify-web ALL=(ALL) NOPASSWD: /usr/sbin/ip route add default *
speedify-web ALL=(ALL) NOPASSWD: /usr/sbin/ip route del default *
speedify-web ALL=(ALL) NOPASSWD: /sbin/ip route show *
speedify-web ALL=(ALL) NOPASSWD: /usr/sbin/ip route show *
speedify-web ALL=(ALL) NOPASSWD: /sbin/ip route
speedify-web ALL=(ALL) NOPASSWD: /usr/sbin/ip route
```

**Important Notes:**
- The file must have permissions `0440` (readable by root and sudoers group only)
- `visudo` will automatically set the correct permissions
- Always use `visudo` to edit sudoers files to prevent syntax errors

#### Step 3: Verify Configuration

Test the configuration:

```bash
# Switch to the speedify-web user
sudo su - speedify-web

# Verify passwordless sudo permissions
sudo -l
# Expected output should include:
#   (ALL) NOPASSWD: /bin/systemctl restart speedify
#   (ALL) NOPASSWD: /sbin/reboot
#   (ALL) NOPASSWD: /sbin/ip route add default *
#   (ALL) NOPASSWD: /sbin/ip route del default *

# Test service restart (should not ask for password)
sudo systemctl restart speedify

# Test reboot command help (DO NOT run on production!)
sudo reboot --help

# Test network routing commands (should not ask for password)
sudo ip route show
```

**Important:** The `sudo -l` command shows you what sudo commands you can run and whether they require a password (NOPASSWD).

#### Step 4: Run Application as the Configured User

When running the application, use the user you configured:

```bash
sudo su - speedify-web
cd /path/to/XNetwork
dotnet run
```

Or use systemd to run it as a service (see Option 3 below).

### Option 2: Run Application as Root (Less Secure)

This is simpler but less secure. The application runs with full root privileges.

```bash
cd /path/to/XNetwork
sudo dotnet run
```

**Security Warning:** Running web applications as root is generally not recommended as it increases the attack surface. If the application is compromised, the attacker has full system access.

### Option 3: Systemd Service (Production Recommended)

For production deployments, run the application as a systemd service with the properly configured user.

#### Step 1: Create Service File

Create `/etc/systemd/system/xnetwork.service`:

```ini
[Unit]
Description=XNetwork Web Application
After=network.target speedify.service

[Service]
Type=notify
User=speedify-web
Group=speedify-web
WorkingDirectory=/opt/xnetwork
ExecStart=/usr/bin/dotnet /opt/xnetwork/XNetwork.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=xnetwork
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
```

#### Step 2: Configure the Service

```bash
# Reload systemd to recognize the new service
sudo systemctl daemon-reload

# Enable the service to start on boot
sudo systemctl enable xnetwork

# Start the service
sudo systemctl start xnetwork

# Check status
sudo systemctl status xnetwork
```

#### Step 3: View Logs

```bash
# Follow logs in real-time
sudo journalctl -u xnetwork -f

# View recent logs
sudo journalctl -u xnetwork -n 100
```

## Security Considerations

### Best Practices

1. **Least Privilege**: Only grant permissions for the specific commands needed
2. **Dedicated User**: Use a dedicated user account for the application
3. **Full Paths**: Always specify full paths in sudoers configuration
4. **No NOPASSWD for ALL**: Never use `NOPASSWD: ALL` - specify exact commands only
5. **Regular Audits**: Review sudoers configuration periodically

### Command Path Verification

To find the exact path of commands on your system:

```bash
which systemctl    # Usually /bin/systemctl or /usr/bin/systemctl
which service      # Usually /sbin/service or /usr/sbin/service
which reboot       # Usually /sbin/reboot or /usr/sbin/reboot
```

Include all possible paths in your sudoers configuration to ensure compatibility across different distributions.

## Troubleshooting

### Verifying Passwordless Sudo Configuration

**First step for any permission issues:** Check your sudo configuration:

```bash
# As the user running the application
sudo -l

# Expected output should include:
# (ALL) NOPASSWD: /bin/systemctl restart speedify
# (ALL) NOPASSWD: /usr/bin/systemctl restart speedify
# (ALL) NOPASSWD: /sbin/service speedify restart
# (ALL) NOPASSWD: /usr/sbin/service speedify restart
# (ALL) NOPASSWD: /sbin/reboot
# (ALL) NOPASSWD: /usr/sbin/reboot
# (ALL) NOPASSWD: /sbin/ip route add default *
# (ALL) NOPASSWD: /sbin/ip route del default *
# (ALL) NOPASSWD: /usr/sbin/ip route add default *
# (ALL) NOPASSWD: /usr/sbin/ip route del default *
# (ALL) NOPASSWD: /sbin/ip route show *
# (ALL) NOPASSWD: /usr/sbin/ip route show *
```

If the output shows `(ALL) ALL` instead of `(ALL) NOPASSWD:`, or if the commands are missing, your passwordless sudo is not configured correctly.

### Permission Denied Errors

If you get permission denied errors:

1. **Verify sudoers syntax:**
   ```bash
   sudo visudo -c -f /etc/sudoers.d/xnetwork-web
   ```

2. **Check file permissions:**
   ```bash
   ls -l /etc/sudoers.d/xnetwork-web
   # Should show: -r--r----- 1 root root
   ```

3. **Verify you can run commands manually:**
   ```bash
   # Should execute without asking for password
   sudo systemctl restart speedify
   sudo reboot --help
   ```

4. **Test as the application user:**
   ```bash
   sudo -u speedify-web sudo systemctl restart speedify
   ```

5. **Check audit logs:**
   ```bash
   sudo journalctl -xe | grep sudo
   ```

### Application Shows Permission Denied

If the application shows "Permission denied" errors:

1. **Ensure you're running as the correct user:**
   ```bash
   whoami  # Should show: speedify-web (or your configured user)
   ```

2. **Verify passwordless sudo is configured:**
   ```bash
   sudo -l
   # Should show NOPASSWD entries for the required commands
   ```

3. **Check the command paths match:**
   - The application uses `sudo systemctl restart speedify` and `sudo reboot`
   - Ensure your sudoers file includes the exact paths shown in `sudo -l`
   - Different distributions use different paths (see Distribution-Specific Notes below)

4. **Test the exact commands the app uses:**
   ```bash
   # Test service restart (should work without password)
   sudo systemctl restart speedify

   # Test alternative service command (should work without password)
   sudo service speedify restart

   # Test reboot help (should work without password)
   sudo reboot --help
   ```

5. **Review the Settings.razor implementation:**
   - `RestartSpeedify()` uses: `sudo systemctl restart speedify 2>/dev/null || sudo service speedify restart`
   - `RebootServer()` uses: `sudo /sbin/reboot`
   - These commands require matching NOPASSWD entries in sudoers

### Service Not Found

If systemctl reports service not found:

```bash
# Check if Speedify service exists
systemctl list-units --type=service | grep speedify

# Check service file location
ls -l /etc/systemd/system/speedify.service
ls -l /usr/lib/systemd/system/speedify.service

# If using init.d instead
ls -l /etc/init.d/speedify
```

## Distribution-Specific Notes

### Ubuntu/Debian
- Default paths: `/usr/bin/systemctl`, `/usr/sbin/service`, `/sbin/reboot`
- Sudoers directory: `/etc/sudoers.d/`

### CentOS/RHEL/Fedora
- Default paths: `/usr/bin/systemctl`, `/usr/sbin/service`, `/usr/sbin/reboot`
- Sudoers directory: `/etc/sudoers.d/`

### Arch Linux
- Default paths: `/usr/bin/systemctl`, `/sbin/reboot`
- No `service` command (systemd only)

## Alternative: Capabilities (Advanced)

Instead of sudo, you can use Linux capabilities to grant specific privileges:

```bash
# Grant CAP_SYS_BOOT to allow reboot without root
sudo setcap cap_sys_boot+ep /path/to/dotnet

# Note: This approach is more complex and may require custom handling
# The sudo approach is generally more straightforward for system administration tasks
```

## Testing the Setup

### Step 1: Verify Command Line First

Before testing from the web application, verify passwordless sudo works from the command line:

```bash
# As the user that will run the application
sudo -l
# Verify NOPASSWD entries are shown

# Test service restart (should not ask for password)
sudo systemctl restart speedify
echo "Exit code: $?"  # Should be 0

# Test reboot help (should not ask for password)
sudo reboot --help
echo "Exit code: $?"  # Should be 0
```

### Step 2: Test from Web Application

After verifying command line access:

1. Start the application as the configured user
2. Navigate to Settings page
3. Click "Restart Speedify Service"
   - Should succeed without password prompt
   - Check logs for any errors
4. Click "Reboot Server" (use with caution!)
   - Should initiate reboot without password prompt

### Step 3: Troubleshooting

If you encounter permission errors:

1. The application will display an error message with troubleshooting steps
2. First action: Run `sudo -l` to verify your configuration
3. Check the NOPASSWD entries match the commands used by the application
4. Review the error details in the application logs

## Additional Resources

- [sudo Manual](https://www.sudo.ws/man/1.8.27/sudo.man.html)
- [sudoers Manual](https://www.sudo.ws/man/1.8.27/sudoers.man.html)
- [systemd Service Configuration](https://www.freedesktop.org/software/systemd/man/systemd.service.html)
- [Linux Capabilities](https://man7.org/linux/man-pages/man7/capabilities.7.html)

## Support

If you continue to experience issues after following this guide:

1. Check the application logs: `sudo journalctl -u xnetwork -f`
2. Verify your sudoers configuration: `sudo visudo -c -f /etc/sudoers.d/xnetwork-web`
3. Test commands manually as the configured user
4. Review system logs: `sudo journalctl -xe`