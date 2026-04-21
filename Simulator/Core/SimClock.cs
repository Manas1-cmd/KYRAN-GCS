using System;
using System.Diagnostics;
using System.Threading;

namespace SimpleDroneGCS.Simulator.Core
{
    /// <summary>
    /// Аргументы одного тика симулятора.
    /// Передаются в callback подписчика из потока <see cref="SimClock"/>.
    /// </summary>
    public readonly struct SimTickArgs
    {
        /// <summary>
        /// Симулированный шаг в секундах с учётом <see cref="SimClock.TimeScale"/>.
        /// При паузе (TimeScale = 0) равен 0.
        /// </summary>
        public double Dt { get; init; }

        /// <summary>
        /// Абсолютное симулированное время с момента <see cref="SimClock.Start"/>, секунды.
        /// При паузе — не увеличивается.
        /// </summary>
        public double SimTime { get; init; }

        /// <summary>
        /// Абсолютное реальное (wall clock) время с момента <see cref="SimClock.Start"/>, секунды.
        /// Растёт всегда, независимо от паузы.
        /// </summary>
        public double WallTime { get; init; }

        /// <summary>Номер тика с момента Start, начиная с 0.</summary>
        public long TickIndex { get; init; }
    }

    /// <summary>
    /// Часы симулятора. Генерируют фиксированный цикл тиков ~50 Hz на выделенном потоке.
    /// <para>
    /// Реальный <c>dt</c> измеряется через <see cref="Stopwatch"/>, не хардкодится.
    /// При задержках системы <c>dt</c> ограничивается сверху (<see cref="MaxDtSeconds"/>),
    /// чтобы не сломать численное интегрирование физики.
    /// </para>
    /// <para>
    /// Масштаб времени (<see cref="TimeScale"/>) позволяет ускорять/замедлять симуляцию
    /// или ставить её на паузу. Масштаб thread-safe, меняется из любого потока.
    /// </para>
    /// <para>
    /// Идемпотентный <see cref="Start"/> бросает исключение при повторном запуске.
    /// <see cref="Stop"/> безопасен для повторных вызовов и возможен после него повторный Start.
    /// </para>
    /// </summary>
    public sealed class SimClock : IDisposable
    {
        /// <summary>Целевая частота тиков, Гц.</summary>
        public const int TargetHz = 50;

        /// <summary>Целевой период тика, миллисекунды.</summary>
        public const double TargetPeriodMs = 1000.0 / TargetHz;

        /// <summary>
        /// Максимальный допустимый dt в секундах. Если реальный dt больше
        /// (система подвисла) — обрезается до этого значения, чтобы не
        /// взорвать численное интегрирование в физике.
        /// </summary>
        public const double MaxDtSeconds = 0.1;

        /// <summary>Минимальный допустимый TimeScale (0 = полная пауза).</summary>
        public const double MinTimeScale = 0.0;

        /// <summary>Максимальный допустимый TimeScale.</summary>
        public const double MaxTimeScale = 5.0;

        // Защита lifecycle-операций (Start/Stop).
        private readonly object _lifecycleLock = new();

        // TimeScale хранится как биты double в long — для атомарного чтения через Interlocked.
        // volatile/lock для double недоступны напрямую, это стандартный приём.
        private long _timeScaleBits = BitConverter.DoubleToInt64Bits(1.0);

        private CancellationTokenSource _cts;
        private Thread _thread;
        private Action<SimTickArgs> _onTick;
        private bool _running;

        /// <summary>
        /// Текущий масштаб времени. Значение clamp'ается в [<see cref="MinTimeScale"/>..<see cref="MaxTimeScale"/>].
        /// 0 = пауза. 1 = нормально. 2/5 = ускорение.
        /// Thread-safe для чтения и записи из любого потока.
        /// </summary>
        public double TimeScale
        {
            get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _timeScaleBits));
            set
            {
                double clamped = Math.Clamp(value, MinTimeScale, MaxTimeScale);
                Interlocked.Exchange(ref _timeScaleBits, BitConverter.DoubleToInt64Bits(clamped));
            }
        }

        /// <summary>Симулятор поставлен на паузу (TimeScale ≤ 0).</summary>
        public bool IsPaused => TimeScale <= 0.0;

        /// <summary>Цикл запущен.</summary>
        public bool IsRunning
        {
            get { lock (_lifecycleLock) return _running; }
        }

        /// <summary>
        /// Запустить цикл тиков. Подписчик <paramref name="onTick"/> вызывается
        /// из потока SimClock на каждом тике. Callback должен быть быстрым
        /// (бюджет ~20 мс на тик). Исключения из callback перехватываются
        /// и не валят цикл.
        /// </summary>
        /// <param name="onTick">Обработчик тика, обязателен.</param>
        /// <exception cref="ArgumentNullException">onTick = null.</exception>
        /// <exception cref="InvalidOperationException">Уже запущен.</exception>
        public void Start(Action<SimTickArgs> onTick)
        {
            if (onTick == null) throw new ArgumentNullException(nameof(onTick));

            lock (_lifecycleLock)
            {
                if (_running)
                    throw new InvalidOperationException("SimClock уже запущен. Вызовите Stop перед повторным Start.");

                _onTick = onTick;
                _cts = new CancellationTokenSource();
                _running = true;

                _thread = new Thread(RunLoop)
                {
                    IsBackground = true,
                    Name = "SimClock",
                    Priority = ThreadPriority.AboveNormal,
                };
                _thread.Start(_cts.Token);
            }
        }

        /// <summary>
        /// Остановить цикл. Безопасно повторно. Не вызывать из callback onTick
        /// (будет pass-through без Join, но избегайте такого использования).
        /// После Stop можно снова <see cref="Start"/>.
        /// </summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            Thread thr;

            lock (_lifecycleLock)
            {
                if (!_running) return;
                cts = _cts;
                thr = _thread;
                _cts = null;
                _thread = null;
                _running = false;
                _onTick = null;
            }

            try { cts?.Cancel(); } catch { /* ignore */ }

            // Не ждём собственный поток (deadlock при вызове из callback).
            if (thr != null && thr != Thread.CurrentThread)
            {
                try { thr.Join(2000); } catch { /* ignore */ }
            }

            try { cts?.Dispose(); } catch { /* ignore */ }
        }

        /// <summary>Основной цикл. Выполняется в выделенном потоке.</summary>
        private void RunLoop(object state)
        {
            var token = (CancellationToken)state;
            var wall = Stopwatch.StartNew();

            long tickIndex = 0;
            double simTimeSec = 0.0;
            double lastWallSec = 0.0;

            while (!token.IsCancellationRequested)
            {
                // Отметка времени следующего тика (идеальное расписание).
                double nextTickMs = tickIndex * TargetPeriodMs;

                // Ожидаем до отметки. SpinWait.SpinUntil балансирует yield/spin,
                // давая хорошую точность (~1 мс) без лишней нагрузки на CPU.
                // Таймаут 100 мс нужен чтобы периодически проверять cancellation.
                SpinWait.SpinUntil(
                    () => token.IsCancellationRequested || wall.Elapsed.TotalMilliseconds >= nextTickMs,
                    millisecondsTimeout: 100);

                if (token.IsCancellationRequested) break;

                // Реальный dt = сколько прошло wall time с прошлого тика.
                double nowSec = wall.Elapsed.TotalSeconds;
                double realDt = nowSec - lastWallSec;
                lastWallSec = nowSec;

                // Защита от лагов и обратного хода часов.
                if (realDt > MaxDtSeconds) realDt = MaxDtSeconds;
                else if (realDt < 0) realDt = 0;

                // Применяем масштаб времени. При паузе simDt = 0, physics замирает.
                double simDt = realDt * TimeScale;
                simTimeSec += simDt;

                // Callback. Захватываем локально на случай одновременного Stop.
                var handler = _onTick;
                if (handler != null)
                {
                    try
                    {
                        handler(new SimTickArgs
                        {
                            Dt = simDt,
                            SimTime = simTimeSec,
                            WallTime = nowSec,
                            TickIndex = tickIndex,
                        });
                    }
                    catch
                    {
                        // Исключение из callback не должно валить цикл SimClock.
                        // Логирование — ответственность верхнего уровня (SimulatedDrone).
                    }
                }

                tickIndex++;
            }
        }

        /// <inheritdoc/>
        public void Dispose() => Stop();
    }
}