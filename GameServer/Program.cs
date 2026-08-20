using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

const int port = 5000;
const int maxPlayers = 2;
const float playerSpeed = 350f;
const float simulationUpdateIntervalSeconds = 1f / 60f;
const int ticksPerStateBroadcast = 3;
const float worldWidth = 800f;
const float worldHeight = 480f;
const float playerSize = 50f;


Dictionary<int, PlayerConnection> players = new();
HashSet<int> reservedPlayerIds = new();
object playersLock = new();

TcpListener server = new TcpListener(IPAddress.Any, port);
server.Start();

Console.WriteLine($"Server started on port {port}.");
Console.WriteLine("Waiting for clients...");

_ = Task.Run(UpdateGameLoopAsync);

while (true)
{
    TcpClient client = await server.AcceptTcpClientAsync();

    _ = Task.Run(() => HandleClientAsync(client));
}

async Task HandleClientAsync(TcpClient client)
{
    int playerId = 0;

    try
    {
        IPEndPoint? endpoint = client.Client.RemoteEndPoint as IPEndPoint;

        Console.WriteLine(
            $"Client connected: {endpoint?.Address}:{endpoint?.Port}");

        NetworkStream stream = client.GetStream();

        using StreamReader reader = new StreamReader(
            stream, Encoding.UTF8, leaveOpen: true);

        using StreamWriter writer = new StreamWriter(
            stream, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };

        lock (playersLock)
        {
            for (int id = 1; id <= maxPlayers; id++)
            {
                if (!reservedPlayerIds.Contains(id))
                {
                    playerId = id;
                    reservedPlayerIds.Add(id);
                    break;
                }
            }
        }

        if (playerId == 0)
        {
            await writer.WriteLineAsync("SERVER_FULL");
            Console.WriteLine("Connection rejected: server is full.");
            return;
        }

        PlayerConnection player = new PlayerConnection
        {
            Id = playerId,
            Writer = writer,
            X = playerId == 1 ? 120f : 660f,
            Y = 270f
        };

        await writer.WriteLineAsync($"PLAYER_ID:{playerId}");

        lock (playersLock)
        {
            players.Add(playerId, player);
        }

        Console.WriteLine($"Player {playerId} joined.");

        while (await reader.ReadLineAsync() is string message)
        {
            UpdatePlayerInput(player, message);
        }
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Client error: {exception.Message}");
    }
    finally
    {
        lock (playersLock)
        {
            if (playerId != 0)
            {
                players.Remove(playerId);
                reservedPlayerIds.Remove(playerId);
            }
        }

        client.Close();

        if (playerId != 0)
        {
            Console.WriteLine($"Player {playerId} disconnected.");
        }
    }
}

void UpdatePlayerInput(PlayerConnection player, string message)
{
    if (!message.StartsWith("INPUT:") || message.Length != 10)
    {
        return;
    }

    lock (playersLock)
    {
        player.Up = message[6] == '1';
        player.Down = message[7] == '1';
        player.Left = message[8] == '1';
        player.Right = message[9] == '1';
    }
}

async Task UpdateGameLoopAsync()
{
    int tickCount = 0;

    using PeriodicTimer timer = new PeriodicTimer(
        TimeSpan.FromSeconds(simulationUpdateIntervalSeconds));

    while (await timer.WaitForNextTickAsync())
    {
        PlayerConnection[] connectedPlayers;
        string stateMessage;

        lock (playersLock)
        {
            PlayerConnection[] playerArray = players.Values.ToArray();

            Dictionary<int, (float X, float Y)> proposedPositions = new();

            foreach (PlayerConnection player in playerArray)
            {
                float moveX = 0f;
                float moveY = 0f;

                if (player.Up) moveY -= 1f;
                if (player.Down) moveY += 1f;
                if (player.Left) moveX -= 1f;
                if (player.Right) moveX += 1f;

                if (moveX != 0f && moveY != 0f)
                {
                    moveX *= 0.7071f;
                    moveY *= 0.7071f;
                }

                float newX = player.X + moveX * playerSpeed *
                             simulationUpdateIntervalSeconds;

                float newY = player.Y + moveY * playerSpeed *
                             simulationUpdateIntervalSeconds;


                newX = Math.Clamp(newX, 0f, worldWidth - playerSize);
                newY = Math.Clamp(newY, 0f, worldHeight - playerSize);

                proposedPositions[player.Id] = (newX, newY);
            }


            foreach (PlayerConnection player in playerArray)
            {
                (float newX, float newY) = proposedPositions[player.Id];

                bool collidesWithAnotherPlayer = playerArray.Any(otherPlayer =>
                {
                    if (otherPlayer.Id == player.Id)
                    {
                        return false;
                    }

                    (float otherX, float otherY) =
                        proposedPositions[otherPlayer.Id];

                    return RectanglesOverlap(
                        newX, newY, playerSize, playerSize,
                        otherX, otherY, playerSize, playerSize);
                });

                if (!collidesWithAnotherPlayer)
                {
                    player.X = newX;
                    player.Y = newY;
                }
            }

            tickCount++;

            if (tickCount % ticksPerStateBroadcast != 0)
            {
                continue;
            }

            connectedPlayers = players.Values.ToArray();

            string positions = string.Join(
                ";",
                connectedPlayers.Select(player =>
                    $"{player.Id}," +
                    $"{player.X.ToString("F1", CultureInfo.InvariantCulture)}," +
                    $"{player.Y.ToString("F1", CultureInfo.InvariantCulture)}"));

            stateMessage = $"STATE:{positions}";
        }

        foreach (PlayerConnection player in connectedPlayers)
        {
            try
            {
                await player.Writer.WriteLineAsync(stateMessage);
            }
            catch
            {
            }
        }
    }
    bool RectanglesOverlap(
    float firstX,
    float firstY,
    float firstWidth,
    float firstHeight,
    float secondX,
    float secondY,
    float secondWidth,
    float secondHeight)
    {
        return firstX < secondX + secondWidth && firstX + firstWidth > secondX &&
               firstY < secondY + secondHeight && firstY + firstHeight > secondY;
    }
}

class PlayerConnection
{
    public int Id { get; init; }

    public StreamWriter Writer { get; init; } = null!;

    public float X { get; set; }
    public float Y { get; set; }

    public bool Up { get; set; }
    public bool Down { get; set; }
    public bool Left { get; set; }
    public bool Right { get; set; }
}