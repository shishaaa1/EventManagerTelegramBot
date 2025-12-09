using Dapper;
using EventManagerTelegramBot.Classes; 
using MySqlConnector;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Web;

namespace EventManagerTelegramBot
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly MySqlConnection _db;
        private readonly string Token;
        private TelegramBotClient BotClient = null!;
        private Timer? _timer;

        private readonly List<string> Messages = new()
        {
            "Привет! Я бот-напоминания! 👋\n" +
            "\nИспользуй кнопки ниже или команды.\n/help — помощь.",
            $"<code>{DateTime.Now:HH.mm dd.MM.yyyy}\nТекст напоминания</code>\n\nДля повторяющейся добавь строку:\n<code>каждую ср,вс</code>",
            "Неверный формат даты/времени или текст. ❌\nПопробуй ещё раз.",
            $"Текущее время: {DateTime.Now:HH:mm dd.MM.yyyy}",
            "У вас пока нет задач. 🥳",
            "Задача удалена. ✅",
            "Все задачи очищены. ✅",
            "Задачи успешно очищены! ✨"
        };

        public Worker(ILogger<Worker> logger, MySqlConnection db, IConfiguration config)
        {
            _logger = logger;
            _db = db;
            Token = config["BotToken"] ?? throw new ArgumentNullException("BotToken не найден в конфигурации.");
            BotClient = new TelegramBotClient(Token);
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_db.State != System.Data.ConnectionState.Open)
                {
                    await _db.OpenAsync(stoppingToken);
                }
                await EnsureTablesCreated();
                var receiverOptions = new ReceiverOptions
                {
                    AllowedUpdates = Array.Empty<UpdateType>() 
                };

                BotClient.StartReceiving(
                     HandleUpdateAsync,
                    HandleErrorAsync,
                    receiverOptions: receiverOptions,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("Бот начал прием сообщений.");

                _timer = new Timer(
                    callback: async _ => await Tick(),
                    state: null,
                    dueTime: TimeSpan.Zero,
                    period: TimeSpan.FromSeconds(30));
                while (!stoppingToken.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка в ExecuteAsync.");
            }
            finally
            {
                _db?.Dispose();
                _timer?.Dispose();
                _logger.LogInformation("Бот остановлен.");
            }
        }
        private async Task EnsureTablesCreated()
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Id BIGINT PRIMARY KEY,
                    Username VARCHAR(255)
                );
                CREATE TABLE IF NOT EXISTS Events (
                    Id INT AUTO_INCREMENT PRIMARY KEY,
                    UserId BIGINT NOT NULL,
                    EventTime DATETIME NOT NULL,
                    Message TEXT NOT NULL,
                    IsRecurring TINYINT(1) DEFAULT 0,
                    RecurringDays VARCHAR(20) DEFAULT NULL,
                    LastTriggered DATETIME NULL,
                    FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
                );";

            await _db.ExecuteAsync(sql);
        }

        private static ReplyKeyboardMarkup GetMainKeyboard() => new(new[]
        {
            new[] { new KeyboardButton("📝 Создать задачу"), new KeyboardButton("📋 Мои задачи") },
            new[] { new KeyboardButton("🗑️ Очистить все") }
        })
        { ResizeKeyboard = true };

        private static InlineKeyboardMarkup DeleteButton(string data) => new(
            InlineKeyboardButton.WithCallbackData("❌ Удалить", data));
        private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
        {
            if (update.Message is { } message)
            {
                var from = message.From!;
                await EnsureUserExists(from.Id, from.Username);

                var text = message.Text?.Trim();

                if (text == "/start")
                    await BotClient.SendMessage(message.Chat.Id, Messages[0], replyMarkup: GetMainKeyboard(), cancellationToken: ct);

                else if (text == "/help")
                    await BotClient.SendMessage(message.Chat.Id, Messages[1], parseMode: ParseMode.Html, cancellationToken: ct);

                else if (text == "📝 Создать задачу" || text == "/create_tasks")
                    await BotClient.SendMessage(message.Chat.Id, "Отправь задачу в формате:\n" + Messages[1], parseMode: ParseMode.Html, cancellationToken: ct);

                else if (text == "📋 Мои задачи" || text?.StartsWith("/list_tasks") == true)
                    await ListTasks(message.Chat.Id, ct);

                else if (text == "🗑️ Очистить все")
                    await ClearAllTasks(message.Chat.Id, ct);

                else if (!string.IsNullOrEmpty(text))
                    await ProcessTaskCreation(message.Chat.Id, text, ct);
            }
            else if (update.CallbackQuery is { } callback)
            {
                var userId = callback.From.Id;
                if (!int.TryParse(callback.Data, out var eventId))
                {
                    await BotClient.AnswerCallbackQuery(callback.Id, "Неверный ID задачи", cancellationToken: ct);
                    return;
                }

                int deleted = await _db.ExecuteAsync("DELETE FROM Events WHERE Id = @Id AND UserId = @UserId", new { Id = eventId, UserId = userId });

                if (deleted > 0)
                {
                    await BotClient.AnswerCallbackQuery(callback.Id, "Задача удалена", cancellationToken: ct);
                    await BotClient.EditMessageText(
                        chatId: callback.Message!.Chat.Id,
                        messageId: callback.Message.MessageId,
                        text: "Задача удалена ✅",
                        cancellationToken: ct);
                }
                else
                {
                    await BotClient.AnswerCallbackQuery(callback.Id, "Задача не найдена или уже удалена.", cancellationToken: ct);
                }
            }
        }

        private async Task EnsureUserExists(long userId, string? username)
        {
            await _db.ExecuteAsync(
                "INSERT INTO Users (Id, Username) VALUES (@Id, @Username) ON DUPLICATE KEY UPDATE Username = @Username",
                new { Id = userId, Username = username ?? "" });
        }

        private async Task ProcessTaskCreation(long chatId, string text, CancellationToken ct)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
            {
                await BotClient.SendMessage(chatId, Messages[2], parseMode: ParseMode.Html, cancellationToken: ct);
                return;
            }
            if (!DateTime.TryParseExact(lines[0], "HH:mm dd.MM.yyyy", null, System.Globalization.DateTimeStyles.None, out var time))
            {
                await BotClient.SendMessage(chatId, Messages[2], parseMode: ParseMode.Html, cancellationToken: ct);
                return;
            }
            if (time < DateTime.Now.AddMinutes(-1))
            {
                await BotClient.SendMessage(chatId, "Нельзя создавать задачи в прошлом! ⏳", cancellationToken: ct);
                return;
            }

            string messageText = string.Join("\n", lines.Skip(1));
            bool isRecurring = false;
            string? recurringDays = null;

            if (lines.Length >= 3 && lines[^1].StartsWith("каждую", StringComparison.OrdinalIgnoreCase))
            {
                var daysStr = lines[^1]
                    .Replace("каждую", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("каждый", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();

                var dayNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    {"пн", 1}, {"понедельник", 1},
                    {"вт", 2}, {"вторник", 2},
                    {"ср", 3}, {"среда", 3},
                    {"чт", 4}, {"четверг", 4},
                    {"пт", 5}, {"пятница", 5},
                    {"сб", 6}, {"суббота", 6},
                    {"вс", 7}, {"воскресенье", 7}
                };

                var days = daysStr
                    .Split(new[] { ',', ' ', 'и' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => dayNames.TryGetValue(d.Trim(), out var n) ? n : -1)
                    .Where(n => n > 0)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

                if (days.Any())
                {
                    isRecurring = true;
                    recurringDays = string.Join(",", days);
                    messageText = string.Join("\n", lines.Skip(1).Take(lines.Length - 2));
                }
            }

            await _db.ExecuteAsync(
                @"INSERT INTO Events (UserId, EventTime, Message, IsRecurring, RecurringDays, LastTriggered)
                  VALUES (@UserId, @EventTime, @Message, @IsRecurring, @RecurringDays, NULL)",
                new
                {
                    UserId = chatId,
                    EventTime = time,
                    Message = messageText,
                    IsRecurring = isRecurring,
                    RecurringDays = recurringDays
                });

            await BotClient.SendMessage(chatId, "Задача успешно создана! ✅", replyMarkup: GetMainKeyboard(), cancellationToken: ct);
        }

        private async Task ListTasks(long chatId, CancellationToken ct)
        {
            var events = await _db.QueryAsync<Events>(
                "SELECT Id, EventTime, Message, IsRecurring, RecurringDays FROM Events WHERE UserId = @UserId ORDER BY EventTime",
                new { UserId = chatId });

            if (!events.Any())
            {
                await BotClient.SendMessage(chatId, Messages[4], replyMarkup: GetMainKeyboard(), cancellationToken: ct);
                return;
            }

            foreach (var ev in events)
            {
                var text = $"<b>{ev.EventTime:HH:mm dd.MM.yyyy}</b>\n{HttpUtility.HtmlEncode(ev.Message)}";

                if (ev.IsRecurring)
                {
                    var daysMap = new Dictionary<int, string>
                    {
                        {1, "Пн"}, {2, "Вт"}, {3, "Ср"}, {4, "Чт"},
                        {5, "Пт"}, {6, "Сб"}, {7, "Вс"}
                    };

                    var days = ev.RecurringDays?.Split(',')
                        .Select(int.Parse)
                        .Select(d => daysMap.TryGetValue(d, out var dayName) ? dayName : "")
                        .Where(s => !string.IsNullOrEmpty(s));

                    text += $"\n🔄 Повторяется: {string.Join(", ", days)}";
                }

                await BotClient.SendMessage(
                    chatId,
                    text,
                    parseMode: ParseMode.Html,
                    replyMarkup: DeleteButton(ev.Id.ToString()),
                    cancellationToken: ct);
            }
        }

        private async Task ClearAllTasks(long chatId, CancellationToken ct)
        {
            await _db.ExecuteAsync("DELETE FROM Events WHERE UserId = @UserId", new { UserId = chatId });
            await BotClient.SendMessage(chatId, Messages[7], replyMarkup: GetMainKeyboard(), cancellationToken: ct);
        }
        private readonly SemaphoreSlim _tickLock = new(1, 1); 

        private async Task Tick()
        {
            if (!_tickLock.Wait(0)) return;

            try
            {
                var now = DateTime.Now;
                var today = now.Date;
                var nowTime = now.TimeOfDay;

                int dayOfWeekNum = (int)now.DayOfWeek;
                if (dayOfWeekNum == 0) dayOfWeekNum = 7;

                _logger.LogDebug("Проверка напоминаний. Текущее время: {Now}", now);
                IEnumerable<Events> oneTimeEvents;
                try
                {
                    oneTimeEvents = await _db.QueryAsync<Events>(
                        @"SELECT * FROM Events
                  WHERE IsRecurring = 0
                    AND DATE(EventTime) = @Today
                    AND TIME_TO_SEC(TIME(EventTime)) <= TIME_TO_SEC(@NowTime)
                    AND LastTriggered IS NULL",
                        new { Today = today, NowTime = nowTime });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при выборке одноразовых событий");
                    oneTimeEvents = Array.Empty<Events>();
                }

                foreach (var ev in oneTimeEvents)
                {
                    try
                    {
                        _logger.LogInformation("Триггер одноразового события ID: {Id} для User: {UserId}", ev.Id, ev.UserId);

                        await BotClient.SendMessage(
                            chatId: ev.UserId,
                            text: $"🔔 Напоминание на сегодня ({ev.EventTime:HH:mm}):\n<b>{HttpUtility.HtmlEncode(ev.Message)}</b>",
                            parseMode: ParseMode.Html
                        );

                        await _db.ExecuteAsync("DELETE FROM Events WHERE Id = @Id", new { ev.Id });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка при отправке одноразового напоминания ID={Id}", ev.Id);
                    }
                }
                IEnumerable<Events> recurringEvents;
                try
                {
                    recurringEvents = await _db.QueryAsync<Events>(
                        @"SELECT * FROM Events
                  WHERE IsRecurring = 1
                    AND FIND_IN_SET(@DayOfWeek, RecurringDays) > 0
                    AND TIME_TO_SEC(TIME(EventTime)) <= TIME_TO_SEC(@NowTime)
                    AND (LastTriggered IS NULL OR DATE(LastTriggered) < @Today)",
                        new { DayOfWeek = dayOfWeekNum, NowTime = nowTime, Today = today });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при выборке повторяющихся событий");
                    recurringEvents = Array.Empty<Events>();
                }

                foreach (var ev in recurringEvents)
                {
                    try
                    {
                        _logger.LogInformation("Триггер повторяющегося события ID: {Id} для User: {UserId}", ev.Id, ev.UserId);

                        await BotClient.SendMessage(
                            chatId: ev.UserId,
                            text: $"🔄 Повторяющееся напоминание ({ev.EventTime:HH:mm}):\n<b>{HttpUtility.HtmlEncode(ev.Message)}</b>",
                            parseMode: ParseMode.Html
                        );

                        await _db.ExecuteAsync(
                            "UPDATE Events SET LastTriggered = NOW() WHERE Id = @Id",
                            new { ev.Id });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка при отправке повторяющегося напоминания ID={Id}", ev.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Фатальная ошибка Tick()");
            }
            finally
            {
                _tickLock.Release();
            }
        }


        private Task HandleErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken __)
        {
            _logger.LogError(exception, "Ошибка Telegram Bot Polling");
            return Task.CompletedTask;
        }
    }
}