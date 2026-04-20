namespace BusinessFinanceAPI.Models
{
    public class User
    {
        public int Id { get; set; } // Внутренний ID в базе
        public long TelegramId { get; set; } // ID пользователя из Telegram
        public string? Username { get; set; } // Никнейм
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Связь 1-ко-многим: у одного пользователя много транзакций
        public List<Transaction> Transactions { get; set; } = new();
    }
}