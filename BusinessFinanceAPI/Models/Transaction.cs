using BusinessFinanceAPI.Models;

namespace BusinessFinanceAPI.Models
{
    public class Transaction
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public string Type { get; set; } = "";
        public string Category { get; set; } = "";
        public string? Comment { get; set; }
        public DateTime Date { get; set; } = DateTime.UtcNow;

        // НОВЫЕ ПОЛЯ ДЛЯ КРЕДИТА
        public int? LoanYears { get; set; }      // Срок в годах
        public decimal? InterestRate { get; set; } // Годовая ставка

        public int UserId { get; set; }
        public User User { get; set; } = null!;
    }
}