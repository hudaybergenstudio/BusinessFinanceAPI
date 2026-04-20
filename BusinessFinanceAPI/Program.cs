using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using BusinessFinanceAPI.Data;
using BusinessFinanceAPI.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// 1. Настройка базы данных
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=finance_bot.db"));

var app = builder.Build();

app.UseStaticFiles();

// --- API ДЛЯ ВЕБ-ДАШБОРДА ---
app.MapGet("/api/stats", async (AppDbContext db) =>
{
    var now = DateTime.UtcNow;
    var allTransactions = await db.Transactions.OrderByDescending(t => t.Date).ToListAsync();

    // Группировка для графиков и истории
    var monthlyHistory = allTransactions
        .GroupBy(t => new { t.Date.Year, t.Date.Month })
        .Select(g => new {
            MonthName = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM yyyy", new System.Globalization.CultureInfo("ru-RU")),
            Income = g.Where(t => t.Type == "income").Sum(t => t.Amount),
            Expenses = g.Where(t => t.Type.StartsWith("exp") || t.Type == "expense").Sum(t => t.Amount),
            Credits = g.Where(t => t.Type == "credit").Sum(t => t.Amount),
            Year = g.Key.Year,
            Month = g.Key.Month
        })
        .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
        .ToList();

    return Results.Json(new
    {
        allTransactions, // Весь список для детальных вкладок
        monthlyHistory,
        thisMonth = new
        {
            MonthName = now.ToString("MMMM", new System.Globalization.CultureInfo("ru-RU")),
            Income = allTransactions.Where(t => t.Date.Month == now.Month && t.Date.Year == now.Year && t.Type == "income").Sum(t => t.Amount),
            Expenses = allTransactions.Where(t => t.Date.Month == now.Month && t.Date.Year == now.Year && (t.Type.StartsWith("exp") || t.Type == "expense")).Sum(t => t.Amount),
            Credits = allTransactions.Where(t => t.Date.Month == now.Month && t.Date.Year == now.Year && t.Type == "credit").Sum(t => t.Amount)
        }
    });
});

// 2. Инициализация бота (ВСТАВЬ СВОЙ ТОКЕН)
string botToken = "8708495055:AAE_ytlKq8QK2Hl94dYo88R-Xi3tFKSEQbk";
var botClient = new TelegramBotClient(botToken);

// 3. ПАМЯТЬ БОТА (Эти переменные должны быть ТУТ, чтобы их видел блок ниже)
var userSessions = new ConcurrentDictionary<long, UserSession>();

var mainMenu = new ReplyKeyboardMarkup(new[]
{
    new KeyboardButton[] { "📉 Расход", "📈 Доход" },
    new KeyboardButton[] { "🏦 Кредит", "📊 Отчет" },
    new KeyboardButton[] { "📜 История", "🌐 Веб-Дашборд" },
    new KeyboardButton[] { "✂️ Удалить запись", "🗑️ Очистить историю" }
})
{
    ResizeKeyboard = true
};

// НОВАЯ КЛАВИАТУРА ДЛЯ ПОДТВЕРЖДЕНИЯ
var confirmClearMenu = new ReplyKeyboardMarkup(new[]
{
    new KeyboardButton[] { "⚠️ Да, удалить всё", "🔙 Вернуться назад" }
})
{
    ResizeKeyboard = true
};

// 4. ЛОГИКА ОБРАБОТКИ СООБЩЕНИЙ
app.MapPost("/bot", async (HttpContext context, AppDbContext db) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var json = await reader.ReadToEndAsync();
    var update = JsonConvert.DeserializeObject<Update>(json);

    // 1. Обработка ВСПЛЫВАЮЩИХ кнопок (Теперь их больше!)
    if (update?.CallbackQuery is { } query)
    {
        var chatId = query.Message!.Chat.Id;
        var session = userSessions.GetOrAdd(chatId, _ => new UserSession());

        if (query.Data == "add_amount")
        {
            session.Step = "WaitingForAmount";
            await botClient.SendTextMessageAsync(chatId, "💰 Введите сумму (числом):");
        }
        // НОВАЯ ЛОГИКА УДАЛЕНИЯ
       
        // Если нажали на подкатегории расхода
        if (query.Data == "exp_op" || query.Data == "exp_fix")
        {
            session.TransactionType = query.Data; // Сохраняем "exp_op" или "exp_fix"
            session.Step = "WaitingForAmount";
            await botClient.SendTextMessageAsync(chatId, "💰 Введите сумму расхода (числом):");
        }
        // Если нажали добавить сумму для Дохода или Кредита
        else if (query.Data == "add_amount")
        {
            session.Step = "WaitingForAmount";
            await botClient.SendTextMessageAsync(chatId, "💰 Введите сумму (числом):");
        }

        await botClient.AnswerCallbackQueryAsync(query.Id);
        return Results.Ok();
    }

    // 2. Обработка обычного текста
    if (update?.Message is { Text: { } text })
    {
        var chatId = update.Message.Chat.Id;
        var session = userSessions.GetOrAdd(chatId, _ => new UserSession());

        var user = await db.Users.FirstOrDefaultAsync(u => u.TelegramId == chatId);
        if (user == null)
        {
            user = new BusinessFinanceAPI.Models.User
            {
                TelegramId = chatId,
                Username = update.Message.From?.Username ?? "Unknown"
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        // --- ВЫВОД ИСТОРИИ ---
        if (text == "📜 История")
        {
            var allTransactions = await db.Transactions
                .Where(t => t.UserId == user.Id)
                .OrderByDescending(t => t.Date) // Самые свежие сверху
                .Take(20) // Берем последние 20 записей, чтобы сообщение не было слишком длинным
                .ToListAsync();

            if (!allTransactions.Any())
            {
                await botClient.SendTextMessageAsync(chatId, "История пока пуста. Внесите первые данные!");
                return Results.Ok();
            }

            var historyBuilder = new StringBuilder();
            historyBuilder.AppendLine("📜 <b>ПОСЛЕДНИЕ ОПЕРАЦИИ</b>\n");

            foreach (var t in allTransactions)
            {
                string icon = t.Type switch
                {
                    "income" => "📈",
                    "exp_op" => "🛒",
                    "exp_fix" => "🏢",
                    "credit" => "🏦",
                    _ => "❓"
                };

                string typeLabel = t.Type switch
                {
                    "income" => "Доход",
                    "exp_op" => "Опер. расход",
                    "exp_fix" => "Инфраструктура",
                    "credit" => "Кредит",
                    _ => "Прочее"
                };

                historyBuilder.AppendLine($"🔹 <b>ID: {t.Id}</b> | {icon} <b>{t.Amount} руб.</b> — {typeLabel}");
                historyBuilder.AppendLine($"└ <i>{t.Category}</i>");

                if (t.Type == "credit" && t.InterestRate.HasValue)
                {
                    historyBuilder.AppendLine($"  (Ставка: {t.InterestRate}%, на {t.LoanYears} лет)");
                }
                historyBuilder.AppendLine(); // Пробел между записями
            }

            await botClient.SendTextMessageAsync(
                chatId,
                historyBuilder.ToString(),
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                replyMarkup: mainMenu
            );
            return Results.Ok();
        }
        // --- ГЕНЕРАЦИЯ ОТЧЕТА ---
        if (text == "📊 Отчет")
        {
            var allTransactions = await db.Transactions.Where(t => t.UserId == user.Id).ToListAsync();

            decimal totalIncome = allTransactions.Where(t => t.Type == "income").Sum(t => t.Amount);
            decimal standardExpenses = allTransactions.Where(t => t.Type == "exp_op" || t.Type == "exp_fix" || t.Type == "expense").Sum(t => t.Amount);

            // Считаем ежемесячную нагрузку по всем кредитам
            decimal monthlyLoanRepayment = 0;
            var loans = allTransactions.Where(t => t.Type == "credit");
            foreach (var loan in loans)
            {
                monthlyLoanRepayment += FinanceCalculator.CalculateMonthlyPayment(loan.Amount, loan.InterestRate, loan.LoanYears);
            }

            // Итоговый расход = обычные траты + платежи по кредитам (за 1 месяц)
            decimal totalExpensesWithLoans = standardExpenses + monthlyLoanRepayment;
            decimal netProfit = totalIncome - totalExpensesWithLoans;

            string resultEmoji = netProfit >= 0 ? "🟢" : "🔴";
            string reportMessage = $"📊 <b>ФИНАНСОВЫЙ ОТЧЕТ</b>\n\n" +
                                   $"📈 <b>Выручка:</b> {totalIncome} руб.\n" +
                                   $"📉 <b>Расходы (операц.):</b> {standardExpenses} руб.\n" +
                                   $"💳 <b>Выплата по кредитам (в мес.):</b> {Math.Round(monthlyLoanRepayment, 2)} руб.\n" +
                                   $"──────────────\n" +
                                   $"{resultEmoji} <b>Чистая прибыль:</b> {Math.Round(netProfit, 2)} руб.\n" +
                                   $"──────────────\n" +
                                   $"<i>* Прибыль рассчитана с учетом ежемесячного погашения всех кредитов.</i>";

            await botClient.SendTextMessageAsync(chatId, reportMessage, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: mainMenu);
            return Results.Ok();
        }

        // --- ЗАПРОС НА ОЧИСТКУ ---
        // --- ЗАПРОС НА ОЧИСТКУ (ПЕРЕХОД В ПОДМЕНЮ) ---
        if (text == "🗑️ Очистить историю")
        {
            await botClient.SendTextMessageAsync(
                chatId,
                "⚠️ <b>Внимание!</b>\nВы действительно хотите удалить ВСЕ свои финансовые данные? Это действие нельзя отменить.",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                replyMarkup: confirmClearMenu // Подменяем клавиатуру внизу
            );
            return Results.Ok();
        }

        // --- ЕСЛИ НАЖАЛИ "ДА, УДАЛИТЬ" ---
        if (text == "⚠️ Да, удалить всё")
        {
            var userTransactions = db.Transactions.Where(t => t.UserId == user.Id);
            db.Transactions.RemoveRange(userTransactions);
            await db.SaveChangesAsync();

            // Удаляем данные и возвращаем главное меню
            await botClient.SendTextMessageAsync(chatId, "🗑️ История успешно очищена. База пуста!", replyMarkup: mainMenu);
            return Results.Ok();
        }

        // --- ЕСЛИ НАЖАЛИ "НАЗАД" ---
        if (text == "🔙 Вернуться назад")
        {
            // Просто возвращаем главное меню без изменений
            await botClient.SendTextMessageAsync(chatId, "Отмена операции. Возвращаемся в главное меню.", replyMarkup: mainMenu);
            return Results.Ok();
        }

        // --- ЗАПРОС НА УДАЛЕНИЕ ОДНОЙ ЗАПИСИ ---
        if (text == "✂️ Удалить запись")
        {
            session.Step = "WaitingForDeleteId";
            await botClient.SendTextMessageAsync(
                chatId,
                "Введите <b>ID записи</b>, которую хотите удалить.\n\n<i>(ID можно посмотреть рядом с каждой операцией, нажав кнопку «📜 История»)</i>",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html
            );
            return Results.Ok();
        }

        // --- САМО УДАЛЕНИЕ (КОГДА ПОЛЬЗОВАТЕЛЬ ВВЕЛ ID) ---
        if (session.Step == "WaitingForDeleteId")
        {
            if (int.TryParse(text, out int delId))
            {
                // Ищем транзакцию по ID. Обязательно проверяем, что она принадлежит этому юзеру!
                var txToDelete = await db.Transactions.FirstOrDefaultAsync(t => t.Id == delId && t.UserId == user.Id);

                if (txToDelete != null)
                {
                    db.Transactions.Remove(txToDelete);
                    await db.SaveChangesAsync();
                    await botClient.SendTextMessageAsync(chatId, $"✅ Запись с ID {delId} успешно удалена! Баланс обновлен.", replyMarkup: mainMenu);
                }
                else
                {
                    await botClient.SendTextMessageAsync(chatId, $"❌ Запись с ID {delId} не найдена. Проверьте цифру в «Истории».", replyMarkup: mainMenu);
                }
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "Пожалуйста, введите числовой ID записи.");
            }

            session.Step = "None"; // Сбрасываем шаг
            return Results.Ok();
        }

        // --- ПЕРЕХОД В ВЕБ-ДАШБОРД ---
        if (text == "🌐 Веб-Дашборд")
        {
            // ЗАМЕНИ ЭТУ ССЫЛКУ на свою ссылку из Dev Tunnels + /dashboard.html
            string dashboardUrl = "https://g9r0xqzj-7127.euw.devtunnels.ms/dashboard.html";

            var webMenu = new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithUrl("🖥️ Открыть панель управления", dashboardUrl)
            });

            await botClient.SendTextMessageAsync(
                chatId,
                "Для просмотра расширенной аналитики и графиков перейдите в веб-интерфейс:",
                replyMarkup: webMenu
            );
            return Results.Ok();
        }

        // --- ГЛАВНОЕ МЕНЮ ---

        if (text == "📉 Расход" || text == "📈 Доход" || text == "🏦 Кредит")
        {
            // Если РАСХОД - показываем подменю с двумя кнопками
            if (text == "📉 Расход")
            {
                var expenseMenu = new InlineKeyboardMarkup(new[]
                {
                    new [] { InlineKeyboardButton.WithCallbackData("🛒 Операционные (Сырье)", "exp_op") },
                    new [] { InlineKeyboardButton.WithCallbackData("🏢 Инфраструктура (ЗП, Аренда)", "exp_fix") }
                });

                await botClient.SendTextMessageAsync(chatId, "Выберите тип расхода:", replyMarkup: expenseMenu);
                return Results.Ok();
            }

            // Если ДОХОД или КРЕДИТ - оставляем старую логику
            if (text == "📈 Доход") session.TransactionType = "income";
            if (text == "🏦 Кредит") session.TransactionType = "credit";

            session.Step = "None";

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData("➕ Добавить сумму", "add_amount")
            });

            await botClient.SendTextMessageAsync(chatId, $"Вы выбрали: {text}", replyMarkup: inlineKeyboard);
            return Results.Ok();
        }

        // --- ВВОД СУММЫ ---
        if (session.Step == "WaitingForAmount")
        {
            if (decimal.TryParse(text, out decimal amount))
            {
                session.Amount = amount;

                if (session.TransactionType == "credit")
                {
                    session.Step = "WaitingForBankName";
                    await botClient.SendTextMessageAsync(chatId, "🏦 Введите название банка:");
                }
                else
                {
                    session.Step = "WaitingForCategory";
                    string prompt = session.TransactionType == "income" ? "Откуда пришли деньги?" : "На что потратили?";
                    await botClient.SendTextMessageAsync(chatId, prompt);
                }
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "Введите только число.");
            }
            return Results.Ok();
        }
        // --- НОВЫЙ ШАГ: НАЗВАНИЕ БАНКА (только для кредита) ---
        if (session.Step == "WaitingForBankName")
        {
            session.BankName = text;
            session.Step = "WaitingForLoanYears";
            await botClient.SendTextMessageAsync(chatId, "⏳ На сколько лет выдан кредит? (введите число)");
            return Results.Ok();
        }

        // --- НОВЫЙ ШАГ: СРОК (только для кредита) ---
        if (session.Step == "WaitingForLoanYears")
        {
            if (int.TryParse(text, out int years))
            {
                session.LoanYears = years;
                session.Step = "WaitingForInterestRate";
                await botClient.SendTextMessageAsync(chatId, "📈 Какая годовая процентная ставка? (например: 12 или 15.5)");
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, "Введите число лет.");
            }
            return Results.Ok();
        }

        // --- ВВОД КАТЕГОРИИ И СОХРАНЕНИЕ ---
        if (session.Step == "WaitingForCategory" || session.Step == "WaitingForInterestRate")
        {
            var transaction = new BusinessFinanceAPI.Models.Transaction
            {
                UserId = user.Id,
                Type = session.TransactionType,
                Amount = session.Amount,
                Category = session.TransactionType == "credit" ? session.BankName : text,
                Comment = text,
                LoanYears = session.TransactionType == "credit" ? session.LoanYears : null,
                InterestRate = (session.TransactionType == "credit" && decimal.TryParse(text, out decimal rate)) ? rate : null
            };

            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
            session.Step = "None";

            string message = session.TransactionType == "credit"
                ? $"✅ Кредит оформлен!\nБанк: {transaction.Category}\nСумма: {transaction.Amount}\nСрок: {transaction.LoanYears} лет\nСтавка: {transaction.InterestRate}%"
                : "✅ Запись сохранена!";

            await botClient.SendTextMessageAsync(chatId, message, replyMarkup: mainMenu);
            return Results.Ok();
        }

        await botClient.SendTextMessageAsync(chatId, "Выберите действие:", replyMarkup: mainMenu);
    }

    return Results.Ok();
});

app.MapGet("/", () => "Сервер Finance Bot запущен и готов к работе!");

app.Run();

// 5. ВСПОМОГАТЕЛЬНЫЙ КЛАСС (Оставляем в самом низу файла)
public class UserSession
{
    public string Step { get; set; } = "None";
    public string TransactionType { get; set; } = "";
    public decimal Amount { get; set; }
    public string BankName { get; set; } = "";
    public int LoanYears { get; set; }
}
public static class FinanceCalculator
{
    public static decimal CalculateMonthlyPayment(decimal amount, decimal? annualRate, int? years)
    {
        if (amount <= 0 || annualRate == null || years == null || years <= 0) return 0;

        // Месячная ставка (например, 13% / 12 / 100 = 0.010833)
        double monthlyRate = (double)annualRate / 12 / 100;
        int totalMonths = years.Value * 12;

        if (monthlyRate == 0) return amount / totalMonths;

        // Формула: Платёж = P * ( r * (1+r)^n ) / ( (1+r)^n - 1 )
        double payment = (double)amount * (monthlyRate * Math.Pow(1 + monthlyRate, totalMonths)) / (Math.Pow(1 + monthlyRate, totalMonths) - 1);

        return (decimal)payment;
    }
}