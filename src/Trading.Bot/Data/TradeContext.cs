using Microsoft.EntityFrameworkCore;

namespace Trading.Bot.Data
{
    public class TradeContext : DbContext
    {
        public DbSet<BotEntity> Bots { get; set; }
        public DbSet<TradesEntity> Trades { get; set; }
        public TradeContext(DbContextOptions<TradeContext> options) : base(options)
        {
        }
    }
}