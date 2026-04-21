using System;
using SimpleDroneGCS.Simulator.Core;

namespace SimpleDroneGCS.Simulator.Failures
{
    /// <summary>
    /// Инжектор отказов. Ставит флаги в <see cref="SimState.Failures"/>, меняет
    /// связанные поля (Gps, Battery, Ekf, Radio) и через события просит
    /// SimulatedDrone выполнить auto-action (RTL при RC failsafe, LAND при critical battery).
    /// <para>
    /// <b>Thread:</b> методы Inject/Clear могут вызываться из UI thread.
    /// <see cref="Tick"/> — из потока SimClock. Все мутации state через <c>state.Write()</c>.
    /// </para>
    /// </summary>
    public sealed class FailureInjector
    {
        private readonly object _lock = new();
        private readonly Random _rng;

        // Таймеры auto-actions (отсчитываются в Tick).
        private double _rcFailsafeElapsedSec;
        private bool _rcFailsafeAutoFired;

        private double _critBatteryElapsedSec;
        private bool _critBatteryAutoFired;

        private double _ekfDivergenceElapsedSec;

        // =====================================================================
        // Events (auto-actions)
        // =====================================================================

        /// <summary>Запросить RTL (после 5 с RC failsafe без восстановления).</summary>
        public event EventHandler RequestAutoRtl;

        /// <summary>Запросить LAND (после 3 с Battery Critical).</summary>
        public event EventHandler RequestAutoLand;

        /// <summary>Сообщение для STATUSTEXT.</summary>
        public event EventHandler<string> StatusText;

        // =====================================================================
        // Constructor
        // =====================================================================

        /// <param name="seed">Seed для детерминизма. 0 = время-зависимый.</param>
        public FailureInjector(int seed = 0)
        {
            _rng = seed == 0 ? new Random() : new Random(seed);
        }

        // =====================================================================
        // GPS Loss
        // =====================================================================

        /// <summary>Потеря GPS: FixType=0, Sats=0, Failures.GpsLoss=true.</summary>
        public void InjectGpsLoss(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.GpsLoss = true;
                    state.Failures = f;

                    state.Gps.FixType = 0;
                    state.Gps.Satellites = 0;
                    state.Gps.Hdop = 99.99;
                    state.Gps.Vdop = 99.99;
                }
            }
            EmitStatus("GPS: Glitch");
        }

        public void ClearGpsLoss(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.GpsLoss = false;
                    state.Failures = f;

                    state.Gps.FixType = 3;
                    state.Gps.Satellites = 14;
                    state.Gps.Hdop = 0.8;
                    state.Gps.Vdop = 1.2;
                }
            }
            EmitStatus("GPS: Fix restored");
        }

        // =====================================================================
        // RC Failsafe
        // =====================================================================

        public void InjectRcFailsafe(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.RcFailsafe = true;
                    state.Failures = f;

                    state.Radio.RssiLocal = 0;
                    state.Radio.RxErrors = (ushort)Math.Min(ushort.MaxValue,
                        state.Radio.RxErrors + 100);
                    state.RcRssi = 0;
                }
                _rcFailsafeElapsedSec = 0;
                _rcFailsafeAutoFired = false;
            }
            EmitStatus("Radio: RC failsafe");
        }

        public void ClearRcFailsafe(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.RcFailsafe = false;
                    state.Failures = f;

                    state.Radio.RssiLocal = 200;
                    state.RcRssi = 200;
                }
                _rcFailsafeElapsedSec = 0;
                _rcFailsafeAutoFired = false;
            }
            EmitStatus("Radio: RC restored");
        }

        // =====================================================================
        // Battery
        // =====================================================================

        /// <summary>Низкий заряд: Percent=15%, Voltage снижается по LiPo кривой.</summary>
        public void InjectBatteryLow(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.BatteryLow = true;
                    f.BatteryCritical = false;
                    state.Failures = f;

                    state.Battery.Percent = 15.0;
                    // ConsumedMah пересчёт не обязателен — Physics продолжит drain
                    // от текущего процента.
                }
            }
            EmitStatus("Battery: Low");
        }

        /// <summary>Критический заряд: Percent=5%, через 3с auto-LAND.</summary>
        public void InjectBatteryCritical(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.BatteryLow = true;
                    f.BatteryCritical = true;
                    state.Failures = f;

                    state.Battery.Percent = 5.0;
                }
                _critBatteryElapsedSec = 0;
                _critBatteryAutoFired = false;
            }
            EmitStatus("Battery: CRITICAL");
        }

        public void ClearBatteryFailure(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.BatteryLow = false;
                    f.BatteryCritical = false;
                    state.Failures = f;
                    // Percent обнуляем к healthy значению.
                    state.Battery.Percent = 100.0;
                    state.Battery.ConsumedMah = 0.0;
                }
                _critBatteryElapsedSec = 0;
                _critBatteryAutoFired = false;
            }
            EmitStatus("Battery: Restored");
        }

        // =====================================================================
        // Motor Failure
        // =====================================================================

        /// <summary>Отказ мотора по индексу (0..MotorCount-1).</summary>
        public void InjectMotorFailure(SimState state, int motorIndex)
        {
            if (state == null) return;
            if (motorIndex < 0 || motorIndex > 7) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.MotorFailureIndex = motorIndex;
                    state.Failures = f;
                }
            }
            EmitStatus($"Motor {motorIndex + 1}: Failed");
        }

        public void ClearMotorFailure(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.MotorFailureIndex = -1;
                    state.Failures = f;
                }
            }
            EmitStatus("Motors: All OK");
        }

        // =====================================================================
        // Compass Error
        // =====================================================================

        public void InjectCompassError(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.CompassError = true;
                    state.Failures = f;
                }
            }
            EmitStatus("Compass: Inconsistent");
        }

        public void ClearCompassError(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.CompassError = false;
                    state.Failures = f;
                }
            }
            EmitStatus("Compass: OK");
        }

        // =====================================================================
        // EKF Divergence
        // =====================================================================

        public void InjectEkfDivergence(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.EkfDivergence = true;
                    state.Failures = f;

                    state.Ekf.Healthy = false;
                    state.Ekf.Flags &= 0x00FF; // снимаем healthy биты
                }
                _ekfDivergenceElapsedSec = 0;
            }
            EmitStatus("EKF: Variance");
        }

        public void ClearEkfDivergence(SimState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                using (state.Write())
                {
                    var f = state.Failures;
                    f.EkfDivergence = false;
                    state.Failures = f;

                    state.Ekf.Healthy = true;
                    state.Ekf.Flags = 0x1FFF;
                    state.Ekf.VelVariance = 0.1;
                    state.Ekf.PosHorizVariance = 0.1;
                    state.Ekf.PosVertVariance = 0.1;
                    state.Ekf.CompassVariance = 0.05;
                }
                _ekfDivergenceElapsedSec = 0;
            }
            EmitStatus("EKF: Healthy");
        }

        // =====================================================================
        // Clear all
        // =====================================================================

        public void ClearAll(SimState state)
        {
            if (state == null) return;
            ClearGpsLoss(state);
            ClearRcFailsafe(state);
            ClearBatteryFailure(state);
            ClearMotorFailure(state);
            ClearCompassError(state);
            ClearEkfDivergence(state);
        }

        // =====================================================================
        // Tick — временная динамика + auto-actions
        // =====================================================================

        /// <summary>
        /// Вызывается из SimClock. Обрабатывает таймеры auto-action и
        /// эволюцию отказов (EKF variances растут со временем).
        /// </summary>
        public void Tick(double dt, SimState state)
        {
            if (dt <= 0 || state == null) return;

            var snap = state.Snapshot();
            bool fireRtl = false;
            bool fireLand = false;

            lock (_lock)
            {
                // ---- RC failsafe → auto RTL через 5 с ----
                if (snap.Failures.RcFailsafe && !_rcFailsafeAutoFired)
                {
                    _rcFailsafeElapsedSec += dt;
                    if (_rcFailsafeElapsedSec >= 5.0)
                    {
                        _rcFailsafeAutoFired = true;
                        fireRtl = true;
                    }
                }

                // ---- Battery Critical → auto LAND через 3 с ----
                if (snap.Failures.BatteryCritical && !_critBatteryAutoFired)
                {
                    _critBatteryElapsedSec += dt;
                    if (_critBatteryElapsedSec >= 3.0)
                    {
                        _critBatteryAutoFired = true;
                        fireLand = true;
                    }
                }

                // ---- EKF divergence: variances растут ----
                if (snap.Failures.EkfDivergence)
                {
                    _ekfDivergenceElapsedSec += dt;

                    // Растут асимптотически к плохим значениям.
                    double growth = Math.Min(_ekfDivergenceElapsedSec / 10.0, 1.0);
                    double targetVar = 5.0;
                    double newPosH = 0.1 + (targetVar - 0.1) * growth;
                    double newVel = 0.1 + (targetVar - 0.1) * growth;
                    double newMag = 0.05 + (targetVar - 0.05) * growth;

                    using (state.Write())
                    {
                        state.Ekf.PosHorizVariance = newPosH;
                        state.Ekf.PosVertVariance = newPosH;
                        state.Ekf.VelVariance = newVel;
                        state.Ekf.CompassVariance = newMag;
                    }
                }
            }

            // События эмитим вне lock — чтобы подписчик (SimulatedDrone) не
            // попал в deadlock если он попытается что-то сделать с состоянием.
            if (fireRtl)
            {
                EmitStatus("Failsafe: auto-RTL");
                RequestAutoRtl?.Invoke(this, EventArgs.Empty);
            }
            if (fireLand)
            {
                EmitStatus("Failsafe: auto-LAND");
                RequestAutoLand?.Invoke(this, EventArgs.Empty);
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void EmitStatus(string msg) => StatusText?.Invoke(this, msg);
    }
}