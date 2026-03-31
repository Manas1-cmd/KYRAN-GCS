namespace SimpleDroneGCS.Services
{
    public interface IFlightLogService
    {
        bool IsRecording { get; }
        string TlogPath { get; }
        string CsvPath { get; }

        void Attach(MAVLinkService mavLink);
        void Detach();
        void ForceStop();
        string[] GetLogFiles();  // .tlog файлы из папки логов
    }
}