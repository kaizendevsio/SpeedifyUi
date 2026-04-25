[![Speedify Knowledge Base](https://d33v4339jhl8k0.cloudfront.net/docs/assets/544fa67fe4b0c856ff411f65/images/671aaad7f539bb6e3c7c6cdd/speedify-KB-header-2.png)](https://support.speedify.com/)

# Speedify Command Line Interface (CLI) Usage Guide

The Speedify CLI is available on Windows, Linux, and macOS. For platform specific details, including installation paths, see the articles for [Windows](https://support.speedify.com/article/831-speedifycli-windows), [Linux](https://support.speedify.com/article/571-getting-started-linux), and [macOS](https://support.speedify.com/article/830-speedifycli-macos).

# Speedify CLI

The Speedify CLI is a cross-platform utility to help command and monitor Speedify without using the traditional user interface. It is meant to be run on the same device that it is controlling.

The output from Speedify CLI is always one of the following:

- On success, its exit value is 0 and it prints JSON to the console. If there are more than one JSON objects they will be separated by double newlines.
- On error, its exit value is one of the following:
  - 1 = Error from the Speedify API, outputs a JSON error on stderr
  - 2 = Invalid Parameter, outputs a text error message on stderr
  - 3 = Missing Parameter, outputs a text error message on stderr
  - 4 = Unknown Parameter, outputs the full usage message on stderr

For errors from the Speedify API (1), a JSON error message is emitted on stderr. This error contains the fields:

- "errorCode" - a numeric code representing this error,
- "errorType" - a short text code of the error category
- "errorMessage" - a plain text message about the problem

Example Speedify API error message:

```
{
        "errorCode":    3841,
        "errorType":    "Timeout waiting for result",
        "errorMessage": "Timeout"
}

```

On Windows, the executable command is `speedify_cli.exe`. On Linux and Mac, it is `speedify_cli`. For the example commands below, we will use the Linux/Mac name. Add `.exe` if you are using Windows.

On Windows and Linux, please ensure that the background service is running.

On macOS, please ensure that Speedify is open and running to use the CLI.

# Usage

The CLI contains the following commands:

```
speedify_cli v15.7.2.0
Usage : speedify_cli [-s] [-t] action
   -s : single line output
   -t : give all outputs a title
Actions:
   activationcode
   adapter datalimit daily <adapter id> <data usage in bytes|unlimited>
   adapter datalimit dailyboost <adapter id> <additional bytes>
   adapter datalimit monthly <adapter id> <data usage in bytes|unlimited> <day of the month to reset on|0 for last 30 days>
   adapter directionalmode <adapter id> <upload mode (on | backup_off | strict_off)> <download mode (on | backup_off | strict_off)>
   adapter encryption <adapter id> <on|off>
   adapter expose-dscp <adapter id> <on|off>
   adapter overlimitratelimit <adapter id> <speed in bits per second|0 to stop using>
   adapter priority <adapter id> <automatic|always|secondary|backup|never>
   adapter ratelimit <adapter id> <download speed in bits per second|unlimited> <upload speed in bits per second|unlimited>
   adapter resetusage <adapter id>
   captiveportal check
   captiveportal login <on|off> <adapter id>
   headercompression <on|off>
   connect [ closest | public | private | p2p | <country> [<city> [<number>]] | last | <tag> ]
   connectmethod < closest | public | private | p2p | <country> [<city> [<number>]] | <tag> >
   connectretry <time in seconds>
   daemon exit
   directory [directory server domain]
   dns <ip address> ...
   disconnect
   dscp queues <add|rem|set> ...
   dscp queues add [<dscp value 0-63> [priority] <on|off|auto> [replication] <on|off|auto> [retransmissions] <0-255>] ...
   dscp queues set [<dscp value 0-63> [priority] <on|off|auto> [replication] <on|off|auto> [retransmissions] <0-255>] ...
   dscp queues rem [dscp value 0-63] ...
   encryption <on|off>
   fixeddelay <domains|ips|ports|delay in ms>
   fixeddelay domains <add|rem|set> <domain> ...
   fixeddelay ips <add|rem|set> <ip address> ...
   fixeddelay ports <add|rem|set> [port[-portRangeEnd]/proto] ...
   gateway [directory gateway uri]
   jumbo <on|off>
   login <username> [password]
   login auto
   login oauth [access token]
   logout
   maxredundant <number of conns>
   mode <redundant|speed|streaming>
   networksharing set alwaysOnDiscovery <on|off>
   networksharing availableshares
   networksharing connect <peer connect code>
   networksharing discovery
   networksharing peer <allow|reject|request|unpair> <peer uuid>
   networksharing reconnect <peer uuid>
   networksharing set <host|client> <on|off>
   networksharing set autoPairBehavior <manual|auto_user|auto_user_team>
   networksharing set displayname <new name>
   networksharing set pairRequestBehavior <ask|accept|reject>
   networksharing set peer <autoreconnect|allowhost|allowclient> <peer uuid> <on|off>
   networksharing settings
   networksharing startdiscovery
   overflow <speed in mbps>
   packetaggr <on|off>
   packetpool <small|default|large>
   ports [port/proto] ...
   priorityoverflow <speed in mbps>
   privacy advancedIspStats <on|off>
   privacy apiProtection <on|off>
   privacy requestToDisableDoH <on|off>
   refresh oauth [access token]
   route default <on|off>
   log <erase|daemon>
   log erase
   log daemon <file size> <files per daemon> <total files> <verbose|info|warn|error>
   show < servers | settings | privacy | adapters | currentserver | user | directory | connectmethod | streamingbypass | disconnect | streaming | speedtest | logsettings | dscp | fixeddelay | trafficrules >
   speedtest [adapter id]
   streamtest [adapter id]
   startupconnect <on|off>
   state
   stats [historic | [duration in seconds] [networksharing] [current|day|week|month|total|<period in hours>] ...]
   streaming domains <add|rem|set> <domain> ...
   streaming ipv4 <add|rem|set> <ip address> ...
   streaming ipv6 <add|rem|set> <ip address> ...
   streaming ports <add|rem|set> [port[-portRangeEnd]/proto] ...
   streamingbypass domains <add|rem|set> [<domain> ...]
   streamingbypass ipv4 <add|rem|set> <ip address> ...
   streamingbypass ipv6 <add|rem|set> <ip address> ...
   streamingbypass ports <add|rem|set> <port[-portRangeEnd]/proto> ...
   streamingbypass service <enable|disable|service name> [<on|off>]
   subnets [subnet/length] ...
   targetconnections <number upload connections> <number download connections>
   transport <auto|tcp|tcp-multi|udp|https>
   transportretry <time in seconds>
   version

```

# Commands

## activationcode

Obtain activation code to activate a device on my.speedify.come.

```
$ speedify_cli activationcode
{
        "activationCode" : "123456",
        "activationUrl" : "https://my.speedify.com/activate?activationCode=123456"
}

```

## adapter datalimit daily <adapter id> <data usage in bytes\|unlimited>

The `adapter datalimit daily` limit the data usage for a specific adapter on a daily basis. The usage can be either limited in bytes or unlimited. This will set the maxDaily value accordingly. The adapter guid can be found by using the `show adapters` option. Whether the adapter is disabled or rate limited is controlled by the `adapter overlimitratelimit` setting.

```
$ speedify_cli adapter datalimit daily wlan0 0
[\
    {\
        "adapterID" : "eth0",\
        "connectedNetworkBSSID" : "",\
        "connectedNetworkName" : "",\
        "dataUsage" :\
        {\
            "overlimitRatelimit" : 5000000,\
            "usageDaily" : 0,\
            "usageDailyBoost" : 0,\
            "usageDailyLimit" : 0,\
            "usageMonthly" : 0,\
            "usageMonthlyLimit" : 0,\
            "usageMonthlyResetDay" : 0\
        },\
        "description" : "eth0",\
        "directionalSettings" :\
        {\
            "download" : "on",\
            "upload" : "on"\
...\
\
```\
\
## adapter datalimit dailyboost <adapter id> <additional bytes>\
\
Bumps up the daily datalimit for today only on a specific adapter on a daily basis. The adapter guid can be found by using the `show adapters` option.\
\
```\
$ speedify_cli adapter datalimit dailyboost wlan0 0\
[\
    {\
        "adapterID" : "eth0",\
        "connectedNetworkBSSID" : "",\
        "connectedNetworkName" : "",\
        "dataUsage" :\
        {\
            "overlimitRatelimit" : 5000000,\
            "usageDaily" : 0,\
            "usageDailyBoost" : 0,\
            "usageDailyLimit" : 0,\
            "usageMonthly" : 0,\
            "usageMonthlyLimit" : 0,\
            "usageMonthlyResetDay" : 0\
        },\
        "description" : "eth0",\
        "directionalSettings" :\
        {\
            "download" : "on",\
            "upload" : "on"\
...\
\
```\
\
## adapter datalimit monthly <adapter id> <data usage in bytes\|unlimited> <day of the month to reset on\|0 for last 30 days>\
\
The `adapter datalimit monthly` sets a monthly data cap that resets on a set date or lasts 30 days. The usage can be either limited in bytes or unlimited. This will set the max and resetDay accordingly. Whether the adapter is disabled or rate limited is controlled by the `adapter overlimitratelimit` setting.\
\
```\
$ speedify_cli adapter datalimit monthly wlan0 2000000000 0\
[\
    {\
        "adapterID" : "eth0",\
        "connectedNetworkBSSID" : "",\
        "connectedNetworkName" : "",\
        "dataUsage" :\
        {\
            "overlimitRatelimit" : 5000000,\
            "usageDaily" : 52317,\
            "usageDailyBoost" : 0,\
            "usageDailyLimit" : 0,\
            "usageMonthly" : 52317,\
            "usageMonthlyLimit" : 0,\
            "usageMonthlyResetDay" : 0\
        },\
        "description" : "eth0",\
        "directionalSettings" :\
        {\
            "download" : "on",\
            "upload" : "on"\
...\
\
```\
\
## adapter encryption <adapter id> <on\|off>\
\
Controls encryption on a single adapter. Note that using the `encryption command will remove all per-adapter encryption settings. Most of the time, you'll just want to use the encryption command that changes all adapters at same time. `\
\
` $ speedify_cli adapter encryption {AA866E92-37EC-4560-9B2F-5E065989AD79} off\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : true,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 30.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : true,\
    "perConnectionEncryptionSettings" :\
...\
adapter overlimitratelimit <adapter id> <speed in bits per second|0 to stop using> When an adapter datalimit is hit, this rate limit (in bit per second) is applied to the adapter. Set to 0 to disable the adapter. $ speedify_cli adapter overlimitratelimit {AA866E92-37EC-4560-9B2F-5E065989AD79} 0\
[\
    {\
        "adapterID" : "{AA866E92-37EC-4560-9B2F-5E065989AD79}",\
        "connectedNetworkBSSID" : "c4:41:1e:ff:ff:ff",\
        "connectedNetworkName" : "Connectify_5G-2",\
        "dataUsage" :\
        {\
            "overlimitRatelimit" : 0,\
            "usageDaily" : 301914121,\
            "usageDailyBoost" : 0,\
            "usageDailyLimit" : 0,\
            "usageMonthly" : 301914121,\
            "usageMonthlyLimit" : 0,\
            "usageMonthlyResetDay" : 0\
        },\
        "description" : "Intel(R) Wi-Fi 6 AX201 160MHz",\
        "isp" : "Comcast Cable",\
        "name" : "Wi-Fi",\
        "priority" : "always",\
        "rateLimit" : 0,\
...\
adapter priority <adapter id> <automatic|always|secondary|backup|never> The adapter priority command allows the user to choose which adapter gets one of the following priorities: | Priority | Description | | -------- | ----------- | | automatic | Let Speedify manage the connection's priority | | always | Use whenever connected | | secondary | Use less than Always connection- only when Always connections are congested or not working | | backup | Only use when other connections are unavailable | | never | Adapter is not used | This will set priority as one of the above mentioned options accordingly. $ speedify_cli adapter priority {AA866E92-37EC-4560-9B2F-5E065989AD79} always\
[\
    {\
        "adapterID" : "{AA866E92-37EC-4560-9B2F-5E065989AD79}",\
        "connectedNetworkBSSID" : "c4:41:1e:ff:ff:ff",\
        "connectedNetworkName" : "Connectify_5G-2",\
        "dataUsage" :\
        {\
            "overlimitRatelimit" : 0,\
            "usageDaily" : 301914121,\
            "usageDailyBoost" : 0,\
            "usageDailyLimit" : 0,\
            "usageMonthly" : 301914121,\
            "usageMonthlyLimit" : 0,\
            "usageMonthlyResetDay" : 0\
        },\
        "description" : "Intel(R) Wi-Fi 6 AX201 160MHz",\
        "isp" : "Comcast Cable",\
        "name" : "Wi-Fi",\
        "priority" : "always",\
        "rateLimit" : 0,\
...\
adapter ratelimit <adapter id> <speed in bits per second|unlimited> The adapter ratelimit command allows the user to throttle the adapter's maximum speed, in bits per second. $ speedify_cli adapter ratelimit {AA866E92-37EC-4560-9B2F-5E065989AD79} 0\
[\
    {\
        "adapterID" : "{AA866E92-37EC-4560-9B2F-5E065989AD79}",\
        "connectedNetworkBSSID" : "c4:41:1e:ff:ff:ff",\
        "connectedNetworkName" : "Connectify_5G-2",\
        "dataUsage" :\
        {\
            "overlimitRatelimit" : 0,\
            "usageDaily" : 301914121,\
            "usageDailyBoost" : 0,\
            "usageDailyLimit" : 0,\
            "usageMonthly" : 301914121,\
            "usageMonthlyLimit" : 0,\
            "usageMonthlyResetDay" : 0\
        },\
        "description" : "Intel(R) Wi-Fi 6 AX201 160MHz",\
        "isp" : "Comcast Cable",\
        "name" : "Wi-Fi",\
        "priority" : "always",\
        "rateLimit" : 0,\
...\
adapter resetusage <adapter id> The adapter resetusage command resets the statistics associated with this adapter. This restarts any daily and monthly data caps. $ speedify_cli adapter resetusage {AA866E92-37EC-4560-9B2F-5E065989AD79}\
[\
    {\
        "adapterID" : "{AA866E92-37EC-4560-9B2F-5E065989AD79}",\
        "connectedNetworkBSSID" : "c4:41:1e:ff:ff:ff",\
        "connectedNetworkName" : "Connectify_5G-2",\
        "dataUsage" :\
        {\
            "overlimitRatelimit" : 0,\
            "usageDaily" : 0,\
            "usageDailyBoost" : 0,\
            "usageDailyLimit" : 0,\
            "usageMonthly" : 0,\
            "usageMonthlyLimit" : 0,\
            "usageMonthlyResetDay" : 0\
        },\
        "description" : "Intel(R) Wi-Fi 6 AX201 160MHz",\
        "isp" : "Comcast Cable",\
        "name" : "Wi-Fi",\
        "priority" : "always",\
        "rateLimit" : 0,\
...\
captiveportal check Checks whether an interfaces are currently being blocked by a captive portal. Returns an array of adapterIDs which are currently captive. $ speedify_cli speedify_cli.exe captiveportal check\
["{D5306735-8BD9-4F89-A1AF-F485CA23208C}"]\
captiveportal login <on|off> <adapter id> Starts or stops directing web traffic out the local interface to allow users to login to the captive portal web page. Setting this to "on" and passing in an Adapter ID, will direct and new connections on ports 53,80 and 443 out the specified adapter. If the Speedify user interface is running it will launch a captive portal web browser component. Setting this to "off" (no Adapter ID required in this case), will stop the forwarding of the web traffic, and will allow it to all pass over the VPN tunnel as usal. $ speedify_cli speedify_cli.exe captiveportal login on "{D5306735-8BD9-4F89-A1AF-F485CA23208C}"\
{\
        "enabled":      true,\
        "adapterID":    "{D5306735-8BD9-4F89-A1AF-F485CA23208C}"\
}\
headercompression <on|off> The headercompression command sets header compression on/off. $ speedify_cli headercompression on\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : true,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 30.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : true,\
    "perConnectionEncryptionSettings" :\
...\
connect [ closest | public | private | p2p | <country> [<city> [<number>]] | last | <tag> ] The connect command connects to a server based on your connectmethod setting, or a server of your choosing. It prints details of the server it has selected. The show servers command will give you a detailed list of servers with their countries, cities and number as fields that you can use in this command. Since 14.0 the connect command persistently stores the arguments so that the next time the daemon connects, it will connect with the same arguments. Previously, you needed to use the connectmethod command to persistently store the setting, but this is no longer required. It accepts both the "tag" style of server name, such as us-newark-18 or accepts country, city and num as separate arguments, us newark 18. To connect to the nearest server in a particular country, pass along a two-letter country code drawn from the speedify_cli show servers command:   $ speedify_cli connect ca\
To connect to a particular city, pass along a two-letter country code and city, drawn from the speedify_cli show servers command:   $ speedify_cli connect us atlanta\
To connect to a specific server, pass along a tag which consists of a two-letter country code, city, and number, separated by hyphens. These can be drawn from the speedify_cli show servers command. By default, if Speedify cannot connect to the named server, it will attempt to connect to another server in the same city. However, by adding a # at the front of the tag, it will lock it only connect to the specific named server (note that on some operating systems, such as Linux, you may need to use quotes around the argument):   $ speedify_cli connect "#us-atlanta-3"\
Example: $ speedify_cli connect\
{\
    "city" : "newark",\
    "country" : "us",\
    "dataCenter" : "linode-newark",\
    "friendlyName" : "United States - Newark #10",\
    "isPremium" : false,\
    "isPrivate" : false,\
    "num" : 10,\
    "publicIP" :\
    [\
        "198.74.8.8"\
    ],\
    "tag" : "us-newark-10",\
    "torrentAllowed" : false\
}\
connectmethod < closest | public | private | p2p | <country> [<city> [<number>]] | <tag> > The connectmethod command sets the connection method used during autoconnect, without actually connecting. It prints the state immediately after the request to set the connection method is made. The method used can be private, p2p or closest. In order for this setting to be used, you may need to begin an autoconnect after changing the method, by running speedify_cli connect.  $ speedify_cli connectmethod closest\
{\
    "city" : "",\
    "connectMethod" : "closest",\
    "country" : "",\
    "num" : 0\
}\
daemon exit Causes the Speedify service to disconnect, and exit. In general, leave this alone. directory [directory server domain] Controls the directory server. In general, leave this alone. dns <ip address> ... The dns command sets the DNS servers to use for domain name resolution. $ speedify_cli dns 8.8.8.8\
{\
    "dnsAddresses" :\
    [\
        "8.8.8.8"\
    ],\
    "dnsleak" : true,\
    "ipleak" : false,\
    "killswitch" : false,\
    "requestToDisableDoH" : false\
}\
disconnect The disconnect command disconnects from the server. It prints the state immediately after the request to disconnect is made. $ speedify_cli disconnect\
{\
    "state" : "LOGGED_IN"\
}\
encryption <on|off> The encryption command enables or disables encryption of all tunneled traffic. It prints the connection settings immediately after the change is made. Note that this will clear all per-adapter encryption settings from the adapter encryption command. $ speedify_cli encryption off\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 30.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
esni <on|off> The esni command sets ESNI (encrypted server name identification) on/off. No longer supported. $ speedify_cli esni on\
{\
    "domain" : "",\
    "enableEsni" : true,\
    "gatewayUri" : ""\
}\
gateway [directory gateway uri] Configures the OAuth gateway url to use. $ speedify_cli gateway https://my.domain.com/oauth/gateway/path\
{\
    "domain" : "",\
    "enableEsni" : true,\
    "gatewayUri" : "https://my.domain.com/oauth/gateway/path"\
}\
jumbo <on|off> The jumbo command allows the TUN adapter to accept larger MTU packets. This will set jumbo_packets to either True or False. $ speedify_cli jumbo on\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 30.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
login <username> [password] The login command instructs Speedify to connect with the given username and password. It prints the state immediately after the request to login is made. Speedify will then proceed to automatically connect if the login succeeds. $ speedify_cli speedify_cli.exe login user@domain.com password123\
{\
        "state":        "LOGGED_IN"\
}\
login auto The login auto command instructs Speedify to connect to a free account with a set data limit. It prints the following state immediately after the request is made. $ speedify_cli speedify_cli.exe login auto\
{\
        "state":        "LOGGED_IN"\
}\
login oauth [access token] The login oauth logs in with the user represented by encrypted token passed in. It prints the state immediately after the request to login is made. Speedify will then proceed to automatically connect if the login succeeds. $ speedify_cli speedify_cli.exe login oauth {encrypted_token}\
{\
        "state":        "LOGGED_IN"\
}\
logout The logout command disconnects from the server and flushes any user credentials that were stored. $ speedify_cli speedify_cli.exe logout\
{\
        "state":        "LOGGED_OUT"\
}\
mode <redundant|speed|streaming> The mode command instructs Speedify to optimize for maximum connection speed or redundancy. Valid options are speed, redundant, and streaming. $ speedify_cli mode speed\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 30.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
networksharing availableshares The networksharing commands control the Pair & Share behavior of this device, allowing it to share cellular connections over a Wi-Fi network to act as virtual network adapters. availableshares returns an array of all currently available networksharing peers that were found via discovery, and what is known about them. $ speedify_cli networksharing availableshares\
[\
{\
    "autoReconnect" : true,\
    "displayName" : "peer device",\
    "haveAuthToken" : false,\
    "peerAsClient" :\
    {\
      "allowed" : true,\
      "peerStatus" : "disconnected",\
      "tunnelStatus" : "inactive",\
      "usage" :\
      {\
        "month" : 0,\
        "today" : 0,\
        "total" : 0,\
        "week" : 0\
      }\
    },\
    "peerAsHost" :\
    {\
      "allowed" : true,\
      "peerStatus" : "unauthenticated",\
      "tunnelStatus" : "inactive",\
      "usage" :\
      {\
        "month" : 0,\
        "today" : 0,\
        "total" : 0,\
        "week" : 0\
      }\
    },\
    "supportsHost" : true,\
    "uuid" : "F1C1290C-CA03-17B4-9989-A22222A28B74"\
}\
]\
networksharing connect <peer connect code> Connect to a peer, using a connect code (hostConnectCode), if that peer is available on the local network. Connect codes normally come from scanning the peer's QR code. You probable actually want to use networksharing peer request <uuid> if you are trying to connect to peer you found in availableshares. $ speedify_cli networksharing connect F1C1290C-CA03-17B4-9989-A22222A28B74\
{\
    "peerStatus" : "authenticated",\
    "role" : "host",\
    "uuid" : "F1C1290C-CA03-17B4-9989-A22222A28B74"\
}\
networksharing discovery Shows the current state of Pair & Share discovery. $ speedify_cli networksharing discovery\
{\
    "discoveryActive" : false\
}\
networksharing peer <allow|reject|request|unpair> <peer uuid> Controls pairing behavior with the peer with the given uuid. The uuid can be found with networksharing availableshares. The options are:\
\
                              allow - accepts a request to pair from this peer reject - refuses a request to pair from this peer request - sends a request to pair to this peer unpair - disconnects and unpairs from this peer. You will need to request / allow again before using this peer again.\
                               $ speedify_cli allow F1C1290C-CA03-17B4-9989-A22222A28B74 on\
{\
"displayName" : "peer device",\
"haveAuthToken" : true,\
"uuid" : "F1C1290C-CA03-17B4-9989-A22222A28B74"\
}\
networksharing reconnect <peer uuid> Reconnect to the peer at . $ speedify_cli networksharing reconnect F1C1290C-CA03-17B4-9989-A22222A28B74\
{\
"peerStatus" : "authenticated",\
"role" : "host",\
"uuid" : "F1C1290C-CA03-17B4-9989-A22222A28B74"\
}\
networksharing set <host|client> <on|off> Controls if device acts as a client (using peers' shared cellular) or a host (allowing peers to use our cellular). Currently only mobile platforms can act as hosts. $ speedify_cli networksharing set client on\
{\
    "clientEnabled" : true,\
    "displayName" : "peer device",\
    "hostConnectCode" : "",\
    "hostEnabled" : false,\
    "pairRequestBehavior" : "ask"\
}\
networksharing set displayname <new name> A valid utf-8 display name. Other devices will see this name when they pair/share with this device. Name is for display only, settings and connections are based on the automatically generated uuid. $ speedify_cli networksharing set displayname peer device\
{\
    "clientEnabled" : true,\
    "displayName" : "peer device",\
    "hostConnectCode" : "",\
    "hostEnabled" : false,\
    "pairRequestBehavior" : "ask"\
}\
networksharing set pairRequestBehavior <ask|accept|reject> Set the behavior for incoming connection requests; ask, accept, or reject. $ speedify_cli networksharing set client on\
{\
    "clientEnabled" : true,\
    "displayName" : "peer device",\
    "hostConnectCode" : "",\
    "hostEnabled" : false,\
    "pairRequestBehavior" : "ask"\
}\
networksharing set peer <autoreconnect|allowhost|allowclient> <peer uuid> <on|off> Turns on and off settings related to an individual peer, identified by uuid. dir\
\
                              autoconnect controls whether to automatically reconnect to this peer when it is available. allowhost controls whether to allow peer to act as a host (e.g. will we use its offered cellular connections). allowclient controls whether to allow peer to use any cellular adapters this computer is sharing as a host.\
                               $ speedify_cli networksharing set peer autoreconnect F1C1290C-CA03-17B4-9989-A22222A28B74 on\
[\
    {\
        "autoReconnect" : true,\
        "displayName" : "peer device",\
        "haveAuthToken" : true,\
        "peerAsClient" :\
        {\
            "allowed" : true,\
            "peerStatus" : "unauthenticated",\
            "tunnelStatus" : "inactive",\
            "usage" :\
            {\
                "month" : 0,\
                "today" : 0,\
                "total" : 0,\
                "week" : 0\
            }\
        },\
        "peerAsHost" :\
        {\
            "allowed" : true,\
            "peerStatus" : "authenticated",\
            "tunnelStatus" : "active",\
            "usage" :\
            {\
                "month" : 0,\
                "today" : 0,\
                "total" : 0,\
                "week" : 0\
            }\
        },\
        "supportsHost" : true,\
        "uuid" : "F1C1290C-CA03-17B4-9989-A22222A28B74"\
    }\
]\
networksharing settings Shows the current Pair & Share settings. $ speedify_cli networksharing settings\
{\
    "clientEnabled" : true,\
    "displayName" : "peer device",\
    "hostConnectCode" : "",\
    "hostEnabled" : false,\
    "pairRequestBehavior" : "accept"\
}\
networksharing startdiscovery Begin discovering other devices on the local network. $ speedify_cli networksharing startdiscovery\
{\
    "discoveryActive" : true\
}\
overflow <speed in mbps> Speed in Mbps after which Secondary connections are not used. $ speedify_cli overflow 10.0\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 10.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
packetaggr <on|off> The packetaggr command sets packet aggregation on/off. $ speedify_cli packetaggr on\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 10.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
ports [port/proto] ... The ports command instructs Speedify to request public ports from a Dedicated (private) Speed Server. These settings only go into effect after a reconnect, and they are ignored by public Speed Servers. Requesting a port that is already taken by another user will lead to the connect request failing, and state will return to LOGGED_IN. Calling the ports command with no additional parameters will clear the port forward requests. $ speedify_cli ports 8001/tcp\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 10.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
privacy dnsleak <on|off> A Windows only setting to ensure DNS cannot go around the tunnel. This could make certain LAN based printers and shared drivers inaccessible. $ speedify_cli privacy dnsleak off\
{\
    "dnsAddresses" :\
    [\
        "8.8.8.8"\
    ],\
    "dnsleak" : false,\
    "ipleak" : false,\
    "killswitch" : false,\
    "requestToDisableDoH" : false\
}\
privacy killswitch <on|off> A Windows only setting to configure firewall rules to make it impossible to access the internet when Speedify is not connected. $ speedify_cli privacy killswitch off\
{\
    "dnsAddresses" :\
    [\
        "8.8.8.8"\
    ],\
    "dnsleak" : false,\
    "ipleak" : false,\
    "killswitch" : false,\
    "requestToDisableDoH" : false\
}\
privacy ipleak <on|off> A Windows only setting to ensure IP cannot go around the tunnel by removing default routes from other interfaces. This could make servers hosted on the computer inaccessible from outside networks. $ speedify_cli privacy ipleak off\
{\
    "dnsAddresses" :\
    [\
        "8.8.8.8"\
    ],\
    "dnsleak" : false,\
    "ipleak" : false,\
    "killswitch" : false,\
    "requestToDisableDoH" : false\
}\
privacy requestToDisableDoH <on|off> If the Speedify VPN connection should request that browsers disable DNS over HTTPS. Enabling this can help streaming and streamingbypass rules function. $ speedify_cli privacy requestToDisableDoH on\
{\
    "dnsAddresses" :\
    [\
        "8.8.8.8"\
    ],\
    "dnsleak" : false,\
    "ipleak" : false,\
    "killswitch" : false,\
    "requestToDisableDoH" : false\
}\
refresh oauth [access token] Accepts a new security token for the same user to be used when communicating with servers. If changing users, use login oauth instead. $ speedify_cli refresh oauth abdef1234567890\
route default <on|off> Configures whether Speedify will obtain a 'default' route to the Internet over the VPN adapter. $ speedify_cli route default on\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 10.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
show servers The show servers command retrieves the current list of Speed Servers. If you have access to any Dedicated Speed Servers, they appear in a private array. The public pool of Speed Servers appear in a public array. $ speedify_cli show servers\
{\
    "private" :\
    [\
        {\
            "city" : "atlanta",\
            "country" : "us",\
            "dataCenter" : "clouvider-atlanta",\
            "friendlyName" : "United States - Atlanta #18",\
            "isPremium" : true,\
            "isPrivate" : true,\
            "num" : 18,\
            "tag" : "privateus-atlanta-18",\
            "torrentAllowed" : true\
        },\
...\
show settings The show settings command retrieves the current connection settings. These settings are sent to the server at connect time, and they can be retrieved at any time. $ speedify_cli show settings\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 10.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
show privacy Outputs privacy related settings $ speedify_cli show privacy\
{\
    "dnsAddresses" :\
    [\
        "8.8.8.8"\
    ],\
    "dnsleak" : false,\
    "ipleak" : false,\
    "killswitch" : false,\
    "requestToDisableDoH" : false\
}\
show adapters The show adapters command allows the user to view all of the network adapters, and their settings and statistics. $ speedify_cli show adapters\
[\
    {\
        "adapterID" : "{AA866E92-37EC-4560-9B2F-5E065989AD79}",\
        "connectedNetworkBSSID" : "c4:41:1e:ff:ff:ff",\
        "connectedNetworkName" : "Connectify_5G-2",\
        "dataUsage" :\
        {\
            "overlimitRatelimit" : 0,\
            "usageDaily" : 107712,\
            "usageDailyBoost" : 0,\
            "usageDailyLimit" : 0,\
            "usageMonthly" : 107712,\
            "usageMonthlyLimit" : 0,\
            "usageMonthlyResetDay" : 0\
        },\
        "description" : "Intel(R) Wi-Fi 6 AX201 160MHz",\
        "isp" : "Comcast Cable",\
        "name" : "Wi-Fi",\
        "priority" : "always",\
        "rateLimit" : 0,\
...\
show currentserver The show currentserver command displays the last server Speedify was connected (which, if you are connected is the current server).  $ speedify_cli show currentserver\
{\
    "city" : "nyc",\
    "country" : "us",\
    "dataCenter" : "clouvider-nyc",\
    "friendlyName" : "United States - New York City #6",\
    "isPremium" : false,\
    "isPrivate" : false,\
    "num" : 6,\
    "publicIP" :\
    [\
        "45.144.8.8"\
    ],\
    "tag" : "us-nyc-6",\
    "torrentAllowed" : false\
}\
show user Outputs information about the currently logged in user. $ speedify_cli show user\
{\
    "bytesAvailable" : -1,\
    "bytesUsed" : 762644311436,\
    "dataPeriodEnd" : "2023-09-27",\
    "email" : "****@connectify.me",\
    "isAutoAccount" : false,\
    "isTeam" : true,\
    "minutesAvailable" : -1,\
    "minutesUsed" : 214191,\
    "paymentType" : "yearly"\
}\
show directory The show directory command shows the current directory server.  $ speedify_cli show directory\
{\
    "domain" : "",\
    "enableEsni" : true,\
    "gatewayUri" : ""\
}\
show connectmethod The show connectmethod command displays the stored connectmethod (the default settings for connect). $ speedify_cli show connectmethod\
{\
    "city" : "",\
    "connectMethod" : "closest",\
    "country" : "",\
    "num" : 0\
}\
show streamingbypass The show streamingbypass command displays current streaming bypass service settings. $ speedify_cli show streamingbypass\
{\
    "domainWatchlistEnabled" : true,\
    "domains" :\
    [\
        "leagueoflegends.co.kr",\
        "leagueoflegends.co.kr",\
        "leagueoflegends.com",\
        "lol.riotgames.com",\
        "lolstatic.com",\
        "lolusercontent.com",\
        "hulu.com",\
        "hulu.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "170.170.8.8",\
        "8.8.8.8",\
        "170.170.8.8"\
    ],\
...\
show disconnect Displays the reason for the last disconnect. $ speedify_cli show disconnect\
{\
    "disconnectReason" : "USERINITIATED"\
}\
show streaming The show streaming command displays current streaming mode settings. $ speedify_cli show streaming\
{\
    "domains" :\
    [\
        "mynewstreamingservice.com",\
        "mynewstreamingservice.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "8.8.8.8"\
    ],\
    "ipv6" :\
    [\
        "2001:4860:4860::8888",\
        "2001:4860:4860::8888"\
    ],\
    "ports" :\
    [\
        {\
            "port" : 8200,\
...\
show speedtest The show speedtest command displays the last speed test results. $ speedify_cli show speedtest\
[\
    {\
        "city" : "nova",\
        "country" : "us",\
        "downloadSpeed" : 0.0,\
        "fps" : 60,\
        "isError" : false,\
        "jitter" : 1,\
        "latency" : 29,\
        "loss" : 0.0,\
        "numConnections" : 1,\
        "resolution" : "720p",\
        "time" : 1694120912,\
        "type" : "streaming",\
        "uploadSpeed" : 4995316.4910875428\
    },\
    {\
        "city" : "nova",\
        "country" : "us",\
        "downloadSpeed" : 200261469.89064643,\
...\
speedtest Runs a speed test over the VPN tunnel, using a bundled iPerf3 client. $ speedify_cli speedtest\
[\
    {\
        "city" : "nyc",\
        "country" : "us",\
        "downloadSpeed" : 232270093.51595116,\
        "isError" : false,\
        "latency" : 24,\
        "numConnections" : 1,\
        "time" : 1694121466,\
        "type" : "speed",\
        "uploadSpeed" : 13881048.35873965\
    }\
]\
streamtest Runs a stream test over the VPN tunnel, using a bundled iPerf3 client. The streamtest is emulating broadcating a live stream; it sends 60 Mbps of UDP traffic and measures the results. $ speedify_cli streamtest\
[\
    {\
        "city" : "nyc",\
        "country" : "us",\
        "downloadSpeed" : 0.0,\
        "fps" : 30,\
        "isError" : false,\
        "jitter" : 1,\
        "latency" : 27,\
        "loss" : 0.0,\
        "numConnections" : 1,\
        "resolution" : "480p",\
        "time" : 1694121498,\
        "type" : "streaming",\
        "uploadSpeed" : 1998404.4961968849\
    }\
]\
startupconnect <on|off> The startupconnect option tells Speedify if it should connect automatically at startup or not. It prints the current settings immediately after the request is made. $ speedify_cli startupconnect on\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" : [],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
    "headerCompression" : true,\
    "jumboPackets" : true,\
    "overflowThreshold" : 10.0,\
    "packetAggregation" : true,\
    "perConnectionEncryptionEnabled" : false,\
    "perConnectionEncryptionSettings" : [],\
...\
state The state command retrieves the current state of the connection. Possible states are LOGGED_OUT, LOGGING_IN, LOGGED_IN, AUTO_CONNECTING, CONNECTING, DISCONNECTING, CONNECTED, OVERLIMIT, and UNKNOWN $ speedify_cli state\
{\
    "state" : "CONNECTED"\
}\
stats [historic | [duration in seconds] [networksharing] [current|day|week|month|total|<period in hours>] ...] The stats command subscribes to a feed of connection and session statistics. By default, this feed will continue until the speedify_cli process is terminated, but an optional parameter can be given to stop and exit after the given number of seconds. This can be useful to monitor how many connections are being utilized by Speedify, and what their current network activity level is in bytes per second. You can specify up to 5 time periods to receive stats over. $ speedify_cli stats 1\
["state",\
{\
    "state" : "CONNECTED"\
}\
]\
["connection_stats",\
{\
    "connections" :\
    [\
        {\
            "adapterID" : "{AA866E92-37EC-4560-9B2F-5E065989AD79}",\
            "connected" : true,\
            "connectionID" : "Wi-Fi%/24",\
            "inFlight" : 1298,\
            "inFlightWindow" : 796448,\
            "jitterMs" : 3,\
            "latencyMs" : 31,\
            "localIp" : "68.81.8.8",\
            "lossReceive" : 0.0,\
...\
streaming domains <add|rem|set> <domain> ... Configure extra domains to be treated as high priority streams when in streaming mode. $ speedify_cli streaming domains add mynewstreamingservice.com\
{\
    "domains" :\
    [\
        "mynewstreamingservice.com",\
        "mynewstreamingservice.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "8.8.8.8"\
    ],\
    "ipv6" :\
    [\
        "2001:4860:4860::8888",\
        "2001:4860:4860::8888"\
    ],\
    "ports" :\
    [\
        {\
            "port" : 8200,\
...\
streaming ipv4 <add|rem|set> <ip address> ... Configure extra IPv4 addresses to be treated as high priority streams when in streaming mode. $ speedify_cli streaming ipv4 add 8.8.8.8\
{\
    "domains" :\
    [\
        "mynewstreamingservice.com",\
        "mynewstreamingservice.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "8.8.8.8"\
    ],\
    "ipv6" :\
    [\
        "2001:4860:4860::8888",\
        "2001:4860:4860::8888"\
    ],\
    "ports" :\
    [\
        {\
            "port" : 8200,\
...\
streaming ipv6 <add|rem|set> <ip address> ... Configure extra IPv6 addresses to be treated as high priority streams when in streaming mode. $ speedify_cli streaming ipv6 add 2001:4860:4860::8888\
{\
    "domains" :\
    [\
        "mynewstreamingservice.com",\
        "mynewstreamingservice.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "8.8.8.8"\
    ],\
    "ipv6" :\
    [\
        "2001:4860:4860::8888",\
        "2001:4860:4860::8888"\
    ],\
    "ports" :\
    [\
        {\
            "port" : 8200,\
...\
streaming ports <add|rem|set> [port[-portRangeEnd]/proto] ... Configure extra ports to be treated as high priority streams when in streaming mode. $ speedify_cli streaming ports add 8200/tcp\
{\
    "domains" :\
    [\
        "mynewstreamingservice.com",\
        "mynewstreamingservice.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "8.8.8.8"\
    ],\
    "ipv6" :\
    [\
        "2001:4860:4860::8888",\
        "2001:4860:4860::8888"\
    ],\
    "ports" :\
    [\
        {\
            "port" : 8200,\
...\
streamingbypass domains <add|rem|set> [<domain> ...] Configure extra domains to bypass the VPN. $ speedify_cli streamingbypass domains add hulu.com\
{\
    "domainWatchlistEnabled" : true,\
    "domains" :\
    [\
        "leagueoflegends.co.kr",\
        "leagueoflegends.co.kr",\
        "leagueoflegends.com",\
        "lol.riotgames.com",\
        "lolstatic.com",\
        "lolusercontent.com",\
        "hulu.com",\
        "hulu.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "170.170.8.8",\
        "8.8.8.8",\
        "170.170.8.8"\
    ],\
...\
streamingbypass ipv4 <add|rem|set> <ip address> ... Configure extra IPv4 addresses to bypass the VPN. $ speedify_cli streamingbypass ipv4 add 8.8.8.8\
{\
    "domainWatchlistEnabled" : true,\
    "domains" :\
    [\
        "leagueoflegends.co.kr",\
        "leagueoflegends.co.kr",\
        "leagueoflegends.com",\
        "lol.riotgames.com",\
        "lolstatic.com",\
        "lolusercontent.com",\
        "hulu.com",\
        "hulu.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "170.170.8.8",\
        "8.8.8.8",\
        "170.170.8.8"\
    ],\
...\
streamingbypass ipv6 <add|rem|set> <ip address> ... Configure extra IPv6 addresses to bypass the VPN. $ speedify_cli streamingbypass ipv4 add 2001:4860:4860::8888\
{\
    "domainWatchlistEnabled" : true,\
    "domains" :\
    [\
        "leagueoflegends.co.kr",\
        "leagueoflegends.co.kr",\
        "leagueoflegends.com",\
        "lol.riotgames.com",\
        "lolstatic.com",\
        "lolusercontent.com",\
        "hulu.com",\
        "hulu.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "170.170.8.8",\
        "8.8.8.8",\
        "170.170.8.8"\
    ],\
...\
streamingbypass ports <add|rem|set> <port[-portRangeEnd]/proto> ... Configure extra ports to bypass the VPN. $ speedify_cli streamingbypass ports add 4800/tcp\
{\
    "domainWatchlistEnabled" : true,\
    "domains" :\
    [\
        "leagueoflegends.co.kr",\
        "leagueoflegends.co.kr",\
        "leagueoflegends.com",\
        "lol.riotgames.com",\
        "lolstatic.com",\
        "lolusercontent.com",\
        "hulu.com",\
        "hulu.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "170.170.8.8",\
        "8.8.8.8",\
        "170.170.8.8"\
    ],\
...\
streamingbypass service <enable|disable|service name> [<on|off>] Configures whether Speedify will allow traffic from a service to bypass the VPN. $ speedify_cli streamingbypass service Netflix on\
{\
    "domainWatchlistEnabled" : true,\
    "domains" :\
    [\
        "leagueoflegends.co.kr",\
        "leagueoflegends.co.kr",\
        "leagueoflegends.com",\
        "lol.riotgames.com",\
        "lolstatic.com",\
        "lolusercontent.com",\
        "hulu.com",\
        "hulu.com"\
    ],\
    "ipv4" :\
    [\
        "8.8.8.8",\
        "170.170.8.8",\
        "8.8.8.8",\
        "170.170.8.8"\
    ],\
...\
subnets [subnet/length] ... Configures a group of subnets connected to this machine as accessible by other clients on a private server. Only for advanced enterprise routing scenarios. $ speedify_cli subnets 192.168.202.1/23\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" :\
    [\
        {\
            "address" : "192.168.8.8",\
            "prefixLength" : 23\
        }\
    ],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
...\
transport <auto|tcp|tcp-multi|udp|https> The transport command instructs Speedify to choose between one of the network protocols auto, tcp, tcp-multi, udp, or https. The transport_mode value is set accordingly based on the user's selection. $ speedify_cli transport udp\
{\
    "allowChaChaEncryption" : true,\
    "bondingMode" : "speed",\
    "downstreamSubnets" :\
    [\
        {\
            "address" : "192.168.8.8",\
            "prefixLength" : 23\
        }\
    ],\
    "enableAutomaticPriority" : true,\
    "enableDefaultRoute" : true,\
    "encrypted" : false,\
    "forwardedPorts" :\
    [\
        {\
            "port" : 8001,\
            "protocol" : "tcp"\
        }\
    ],\
...\
version The version command can be used to verify the version of Speedify that is installed and running. $ speedify_cli version\
{\
    "bug" : 0,\
    "build" : 11426,\
    "maj" : 14,\
    "min" : 0\
}\
Copyright Connectify, Inc.`\
\
Last updated on October 15, 2025\
\
### Related Articles\
\
- [Speedify Sign In Guide](https://support.speedify.com/article/238-login)\
- [Managing Connections in Speedify](https://support.speedify.com/article/236-connection-settings)\
- [Speedify Server Settings Overview](https://support.speedify.com/article/231-servers)\
- [Speedify Dedicated Speed Servers Overview](https://support.speedify.com/article/282-dedicated-speed-servers)\
- [Connecting to Dedicated Speed Servers in Speedify](https://support.speedify.com/article/287-connecting-dedicated-server)\
- [Streaming Video via Speedify Dedicated Speed Server](https://support.speedify.com/article/288-streaming-video-dedicated-servers)\
- [Speedify for Teams Overview](https://support.speedify.com/article/259-teams)\
\
Toggle Search\
\
### Categories\
\
- [Navigating Speedify](https://support.speedify.com/category/230-using-speedify)\
- [Speedify Command Line](https://support.speedify.com/category/994-speedify-command-line)\
- [Routers](https://support.speedify.com/category/921-routers)\
- [macOS](https://support.speedify.com/category/845-macos)\
- [iOS](https://support.speedify.com/category/932-ios)\
- [Windows](https://support.speedify.com/category/846-windows)\
- [Android](https://support.speedify.com/category/931-android)\
- [Linux](https://support.speedify.com/category/844-linux-ubuntu-raspberry-pi-os)\
- [Speedify's Pair & Share Cellular Sharing](https://support.speedify.com/category/894-pair-share)\
- [Dedicated Servers](https://support.speedify.com/category/919-dedicated-servers)\
- [Speedify for Teams](https://support.speedify.com/category/252-speedify-for-teams)\
- [Speedify for Families](https://support.speedify.com/category/392-speedify-for-families)\
- [Frequently Asked Questions](https://support.speedify.com/category/58-general-questions)\
- [Account, Subscriptions, and Billing](https://support.speedify.com/category/65-account-subscriptions-and-billing)\
- [Troubleshooting](https://support.speedify.com/category/188-troubleshooting)\
\
No results found\
\
©\
\
[Connectify](https://speedify.com/)\
\
2025\. Powered by [Help Scout](https://www.helpscout.com/docs-refer/?co=Connectify&utm_source=docs&utm_medium=footerlink&utm_campaign=Docs+Branding)\
\
## Do you have questions for the Speedify CEO or developers?\
\
Tune in and chat with us LIVE on Speedify Office Hours, every Wednesday at 10AM Eastern for Q&A, updates, and live customer support with our developers!\
\
[Learn more »](https://speedify.com/events/?utm_source=helpscout&utm_medium=website&utm_campaign=officehours&utm_content=footer)\
\
[![](https://speedify.com/wp-content/uploads/OFFICE-HOURS-ALEX-PROMO.jpg)](https://speedify.com/events/?utm_source=helpscout&utm_medium=website&utm_campaign=officehours&utm_content=footer)\
\
## Multiple connections, maximum performance.\
\
Speedify is the only app that combines all of your Internet connections to keep you online when it matters most.\
\
[Download](https://speedify.com/download/?utm_source=helpscout&utm_medium=banner&utm_campaign=download_link&utm_content=desktop_kb) [Buy Now](https://speedify.com/store/?utm_source=helpscout&utm_medium=banner&utm_campaign=buy_link&utm_content=desktop_kb)