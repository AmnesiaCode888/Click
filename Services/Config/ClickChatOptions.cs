namespace Click;

public class ClickChatOptions
{
    public const string SectionName = "Chat";

    public int MaxHistoryMessages { get; set; } = 20;
    public int MaxHistoryChars { get; set; } = 25000;
}
