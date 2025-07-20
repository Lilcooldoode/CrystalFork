using System;
using System.Net.Sockets;
using System.IO;
using C = ClientPackets;
using S = ServerPackets;

public partial class GameClient
{
    public async Task ConnectAsync()
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_config.ServerIP, _config.ServerPort);
        _stream = _client.GetStream();
        Console.WriteLine("Connected to server");
        _canRun = false;
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(KeepAliveLoop);
    }

    public async Task LoginAsync()
    {
        if (_stream == null) return;
        Console.WriteLine("Logging in...");
        var ver = new C.ClientVersion { VersionHash = Array.Empty<byte>() };
        await RandomStartupDelayAsync();
        await SendAsync(ver);

        var login = new C.Login { AccountID = _config.AccountID, Password = _config.Password };
        await RandomStartupDelayAsync();
        await SendAsync(login);
    }

    private async Task CreateAccountAsync()
    {
        if (_stream == null) return;
        Console.WriteLine($"Creating account '{_config.AccountID}'...");
        var acc = new C.NewAccount
        {
            AccountID = _config.AccountID,
            Password = _config.Password,
            BirthDate = DateTime.UtcNow.Date,
            UserName = _config.AccountID,
            SecretQuestion = string.Empty,
            SecretAnswer = string.Empty,
            EMailAddress = string.Empty
        };
        await RandomStartupDelayAsync();
        await SendAsync(acc);
    }

    private async Task CreateCharacterAsync()
    {
        if (_stream == null) return;
        Console.WriteLine($"Creating character '{_config.CharacterName}'...");
        var chr = new C.NewCharacter
        {
            Name = _config.CharacterName,
            Gender = (MirGender)_random.Next(Enum.GetValues<MirGender>().Length),
            Class = (MirClass)_random.Next(Enum.GetValues<MirClass>().Length)
        };
        await RandomStartupDelayAsync();
        await SendAsync(chr);
    }
}
