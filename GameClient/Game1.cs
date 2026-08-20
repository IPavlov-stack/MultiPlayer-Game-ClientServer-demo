using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace GameClient
{
    public class Game1 : Game
    {
        private GraphicsDeviceManager _graphics;
        private SpriteBatch _spriteBatch;
        private string _connectionStatus = "Connecting to server...";
        private TcpClient? _client;
        private int _playerId;
        private StreamWriter? _writer;
        private string _lastInputMessage = "";

        private Texture2D _pixel = null!;

        private Vector2 _redPosition = new Vector2(120f, 270f);
        private Vector2 _bluePosition = new Vector2(660f, 270f);

        private readonly Stopwatch _networkClock = Stopwatch.StartNew();
        private readonly List<WorldSnapshot> _snapshots = new();
        private readonly object _snapshotsLock = new();

        private Vector2 _lastReceivedRedPosition = new Vector2(120f, 270f);
        private Vector2 _lastReceivedBluePosition = new Vector2(660f, 270f);

        private const double InterpolationDelaySeconds = 0.1;
        private const float LocalPredictionSpeed = 220f;
        private const float PlayerSize = 50f;
        private const float WorldWidth = 800f;
        private const float WorldHeight = 480f;

        private Vector2 _predictedLocalPosition;
        private bool _hasPredictedLocalPosition;

        private Vector2 _latestAuthoritativeLocalPosition;
        private int _authoritativeStateVersion;
        private int _lastAppliedAuthoritativeStateVersion;

        private readonly object _authoritativePositionLock = new();
        private readonly object _positionsLock = new();
        public Game1()
        {
            _graphics = new GraphicsDeviceManager(this);
            Content.RootDirectory = "Content";
            IsMouseVisible = true;

            IsFixedTimeStep = true;
            TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60.0);

            //Just for local tests (2 clients on 1 pc)
            InactiveSleepTime = TimeSpan.Zero;
        }

        protected override void Initialize()
        {
            _ = ConnectToServerAsync();
            base.Initialize();
        }

        protected override void LoadContent()
        {
            _spriteBatch = new SpriteBatch(GraphicsDevice);
            _pixel = new Texture2D(GraphicsDevice, 1, 1);
            _pixel.SetData(new[] { Color.White });
        }

        protected override void Update(GameTime gameTime)
        {
            if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed || Keyboard.GetState().IsKeyDown(Keys.Escape))
                Exit();

            Window.Title = _connectionStatus;
            UpdateInterpolatedPositions();
            UpdateLocalPrediction(gameTime);
            SendCurrentInput();

            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(Color.CornflowerBlue);

            Vector2 redPosition;
            Vector2 bluePosition;

            lock (_positionsLock)
            {
                redPosition = _redPosition;
                bluePosition = _bluePosition;
            }
            if (_hasPredictedLocalPosition)
            {
                if (_playerId == 1)
                {
                    redPosition = _predictedLocalPosition;
                }
                else if (_playerId == 2)
                {
                    bluePosition = _predictedLocalPosition;
                }
            }
            _spriteBatch.Begin();

            _spriteBatch.Draw(
                _pixel,
                new Rectangle((int)redPosition.X, (int)redPosition.Y, 50, 50),
                Color.Red);

            _spriteBatch.Draw(
                _pixel,
                new Rectangle((int)bluePosition.X, (int)bluePosition.Y, 50, 50),
                Color.Blue);

            _spriteBatch.End();

            base.Draw(gameTime);
        }
        private async Task ConnectToServerAsync()
        {
            try
            {
                _client = new TcpClient();

                await _client.ConnectAsync("192.168.100.43", 5000);

                NetworkStream stream = _client.GetStream();
                _writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true)
                {
                    AutoFlush = true
                };
                using StreamReader reader = new StreamReader(
                    stream, Encoding.UTF8, leaveOpen: true);

                string? firstMessage = await reader.ReadLineAsync();

                if (firstMessage == "SERVER_FULL")
                {
                    _connectionStatus = "Server is full.";
                    _client.Close();
                    return;
                }

                if (firstMessage is not null &&
                    firstMessage.StartsWith("PLAYER_ID:"))
                {
                    _playerId = int.Parse(
                        firstMessage["PLAYER_ID:".Length..]);

                    string color = _playerId == 1 ? "Red" : "Blue";
                    _connectionStatus = $"Player {_playerId} ({color})";
                }
                else
                {
                    _connectionStatus = "Invalid server response.";
                    _client.Close();
                    return;
                }


                while (await reader.ReadLineAsync() is string message)
                {
                    ProcessServerMessage(message);
                }
                _connectionStatus = "Server disconnected.";
            }
            catch (Exception exception)
            {
                _connectionStatus = $"Connection failed: {exception.Message}";
            }
        }
        private void SendCurrentInput()
        {
            if (_writer is null || _client?.Connected != true || _playerId == 0)
            {
                return;
            }

            KeyboardState keyboard = Keyboard.GetState();

            string inputMessage =
                $"INPUT:" +
                $"{(keyboard.IsKeyDown(Keys.W) ? 1 : 0)}" +
                $"{(keyboard.IsKeyDown(Keys.S) ? 1 : 0)}" +
                $"{(keyboard.IsKeyDown(Keys.A) ? 1 : 0)}" +
                $"{(keyboard.IsKeyDown(Keys.D) ? 1 : 0)}";

            if (inputMessage == _lastInputMessage)
            {
                return;
            }

            _lastInputMessage = inputMessage;

            _ = SendInputAsync(inputMessage);
        }

        private async Task SendInputAsync(string inputMessage)
        {
            try
            {
                if (_writer is not null)
                {
                    await _writer.WriteLineAsync(inputMessage);
                }
            }
            catch (Exception exception)
            {
                _connectionStatus = $"Send failed: {exception.Message}";
            }
        }
        private void ProcessServerMessage(string message)
        {
            if (!message.StartsWith("STATE:"))
            {
                return;
            }

            string positionsText = message["STATE:".Length..];
            string[] players = positionsText.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries);

            Vector2 redPosition;
            Vector2 bluePosition;

            lock (_snapshotsLock)
            {
                redPosition = _lastReceivedRedPosition;
                bluePosition = _lastReceivedBluePosition;

                foreach (string playerText in players)
                {
                    string[] values = playerText.Split(',');

                    if (values.Length != 3 ||
                        !int.TryParse(values[0], out int id) ||
                        !float.TryParse(
                            values[1],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float x) ||
                        !float.TryParse(
                            values[2],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float y))
                    {
                        continue;
                    }

                    if (id == 1)
                    {
                        redPosition = new Vector2(x, y);
                    }
                    else if (id == 2)
                    {
                        bluePosition = new Vector2(x, y);
                    }
                }

                _lastReceivedRedPosition = redPosition;
                _lastReceivedBluePosition = bluePosition;

                _snapshots.Add(new WorldSnapshot
                {
                    ReceivedAtSeconds = _networkClock.Elapsed.TotalSeconds,
                    RedPosition = redPosition,
                    BluePosition = bluePosition
                });

                while (_snapshots.Count > 20)
                {
                    _snapshots.RemoveAt(0);
                }
            }

            Vector2 localAuthoritativePosition =
                _playerId == 1 ? redPosition : bluePosition;

            lock (_authoritativePositionLock)
            {
                _latestAuthoritativeLocalPosition =
                    localAuthoritativePosition;

                _authoritativeStateVersion++;
            }
        }
        private void UpdateInterpolatedPositions()
        {
            double renderTime =
                _networkClock.Elapsed.TotalSeconds - InterpolationDelaySeconds;

            lock (_snapshotsLock)
            {
                if (_snapshots.Count == 0)
                {
                    return;
                }


                while (_snapshots.Count >= 2 &&
                       _snapshots[1].ReceivedAtSeconds <= renderTime)
                {
                    _snapshots.RemoveAt(0);
                }

                WorldSnapshot first = _snapshots[0];

                Vector2 redPosition;
                Vector2 bluePosition;

                if (_snapshots.Count == 1)
                {
                    redPosition = first.RedPosition;
                    bluePosition = first.BluePosition;
                }
                else
                {
                    WorldSnapshot second = _snapshots[1];

                    double duration =
                        second.ReceivedAtSeconds - first.ReceivedAtSeconds;

                    float progress = duration <= 0
                        ? 1f
                        : (float)Math.Clamp(
                            (renderTime - first.ReceivedAtSeconds) / duration,
                            0.0,
                            1.0);

                    redPosition = Vector2.Lerp(
                        first.RedPosition,
                        second.RedPosition,
                        progress);

                    bluePosition = Vector2.Lerp(
                        first.BluePosition,
                        second.BluePosition,
                        progress);
                }

                lock (_positionsLock)
                {
                    _redPosition = redPosition;
                    _bluePosition = bluePosition;
                }
            }

        }

        private void UpdateLocalPrediction(GameTime gameTime)
        {
            Vector2 authoritativePosition;
            int authoritativeVersion;

            lock (_authoritativePositionLock)
            {
                authoritativePosition = _latestAuthoritativeLocalPosition;
                authoritativeVersion = _authoritativeStateVersion;
            }


            if (authoritativeVersion == 0)
            {
                return;
            }

            if (!_hasPredictedLocalPosition)
            {
                _predictedLocalPosition = authoritativePosition;
                _hasPredictedLocalPosition = true;
            }


            if (authoritativeVersion != _lastAppliedAuthoritativeStateVersion)
            {
                float difference = Vector2.Distance(
                    _predictedLocalPosition,
                    authoritativePosition);


                if (difference > PlayerSize)
                {
                    _predictedLocalPosition = authoritativePosition;
                }
                else
                {

                    _predictedLocalPosition = Vector2.Lerp( _predictedLocalPosition, authoritativePosition, 0.35f);
                }

                _lastAppliedAuthoritativeStateVersion =
                    authoritativeVersion;
            }

            KeyboardState keyboard = Keyboard.GetState();

            float moveX = 0f;
            float moveY = 0f;

            if (keyboard.IsKeyDown(Keys.W)) moveY -= 1f;
            if (keyboard.IsKeyDown(Keys.S)) moveY += 1f;
            if (keyboard.IsKeyDown(Keys.A)) moveX -= 1f;
            if (keyboard.IsKeyDown(Keys.D)) moveX += 1f;

            if (moveX != 0f && moveY != 0f)
            {
                moveX *= 0.7071f;
                moveY *= 0.7071f;
            }

            float deltaSeconds =
                (float)gameTime.ElapsedGameTime.TotalSeconds;

            Vector2 proposedPosition = _predictedLocalPosition;

            proposedPosition.X +=
                moveX * LocalPredictionSpeed * deltaSeconds;

            proposedPosition.Y +=
                moveY * LocalPredictionSpeed * deltaSeconds;

            proposedPosition.X = Math.Clamp(
                proposedPosition.X,
                0f,
                WorldWidth - PlayerSize);

            proposedPosition.Y = Math.Clamp(
                proposedPosition.Y,
                0f,
                WorldHeight - PlayerSize);


            Vector2 remotePosition;

            lock (_positionsLock)
            {
                remotePosition = _playerId == 1
                    ? _bluePosition
                    : _redPosition;
            }

            if (!RectanglesOverlap(
                    proposedPosition.X,
                    proposedPosition.Y,
                    PlayerSize,
                    PlayerSize,
                    remotePosition.X,
                    remotePosition.Y,
                    PlayerSize,
                    PlayerSize))
            {
                _predictedLocalPosition = proposedPosition;
            }
        }
        private static bool RectanglesOverlap(
                                float firstX,
                                float firstY,
                                float firstWidth,
                                float firstHeight,
                                float secondX,
                                float secondY,
                                float secondWidth,
                                float secondHeight)
        {
            return firstX < secondX + secondWidth &&
                   firstX + firstWidth > secondX &&
                   firstY < secondY + secondHeight &&
                   firstY + firstHeight > secondY;
        }
        private sealed class WorldSnapshot
        {
            public double ReceivedAtSeconds { get; init; }

            public Vector2 RedPosition { get; init; }

            public Vector2 BluePosition { get; init; }
        }
    }
}
