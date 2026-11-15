using System.Threading;
using System.Threading.Tasks;

namespace WhaleWatch.Abstractions.Telegram;

public interface ITelegramNotifier
{
    Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default);
    Task<bool> SendMessageWithKeyboardAsync(string message, object replyMarkup, CancellationToken cancellationToken = default);
}
