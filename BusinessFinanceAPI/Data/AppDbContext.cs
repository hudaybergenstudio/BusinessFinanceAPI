using Microsoft.EntityFrameworkCore;
using BusinessFinanceAPI.Models;

namespace BusinessFinanceAPI.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        // Указываем, какие таблицы будут в базе
        public DbSet<User> Users { get; set; }
        public DbSet<Transaction> Transactions { get; set; }
    }
}