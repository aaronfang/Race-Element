using RaceElement.Data.Common;
using RaceElement.Util.SystemExtensions;
using static RaceElement.HUD.Common.Overlays.Driving.DSX.Resources;

namespace RaceElement.HUD.Common.Overlays.Driving.DSX;

internal static class TriggerHaptics
{
    public static DsxPacket HandleBraking(DsxConfiguration config) => HandleBrakingGeneric(config);

    public static DsxPacket HandleAcceleration(DsxConfiguration config) => HandleAccelerationGeneric(config);

    private static DsxPacket HandleBrakingGeneric(DsxConfiguration config)
    {
        DsxPacket p = new();
        int controllerIndex = 0;

        // TODO: add either an option to threshold it on brake input or based on some curve?
        if (SimDataProvider.LocalCar.Inputs.Brake > config.BrakeSlip.BrakeThreshold / 100f)
        {
            float[] slipRatios = SimDataProvider.LocalCar.Tyres.SlipRatio;

            if (slipRatios.Length == 4)
            {
                // All data providers should return absolute slip values
                float slipRatioFront = Math.Max(Math.Abs(slipRatios[0]), Math.Abs(slipRatios[1]));
                float slipRatioRear = Math.Max(Math.Abs(slipRatios[2]), Math.Abs(slipRatios[3]));

                // TODO: add option for front and rear ratio threshold.
                if (slipRatioFront > config.BrakeSlip.FrontSlipThreshold || slipRatioRear > config.BrakeSlip.RearSlipThreshold)
                {
                    float frontslipCoefecient = slipRatioFront * 4f;
                    frontslipCoefecient.ClipMax(10);

                    float rearSlipCoefecient = slipRatioRear * 2f;
                    rearSlipCoefecient.ClipMax(7.5f);

                    float magicValue = frontslipCoefecient + rearSlipCoefecient;
                    float percentage = magicValue * 1.0f / 17.5f;
                    if (percentage >= 0.05f)
                        p.AddAdaptiveTriggerToPacket(controllerIndex, Trigger.Left, TriggerMode.FEEDBACK, [1, (int)(config.BrakeSlip.FeedbackStrength * percentage)]);

                    int freq = CalculateFrequency(percentage, config.BrakeSlip.MinFrequency, config.BrakeSlip.MaxFrequency, config.BrakeSlip.InvertFrequency);
                    p.AddAdaptiveTriggerToPacket(controllerIndex, Trigger.Left, TriggerMode.VIBRATION, [0, config.BrakeSlip.Amplitude, freq]);
                }
            }
        }

        if (p.Instructions == null) p.AddAdaptiveTriggerToPacket(0, Trigger.Left, TriggerMode.Normal, []);

        return p;
    }

    private static DsxPacket HandleAccelerationGeneric(DsxConfiguration config)
    {
        DsxPacket p = new();
        int controllerIndex = 0;

        if (SimDataProvider.LocalCar.Inputs.Throttle > config.ThrottleSlip.ThrottleThreshold / 100f)
        {
            float[] slipRatios = SimDataProvider.LocalCar.Tyres.SlipRatio;
            if (slipRatios.Length == 4)
            {
                // All data providers should return absolute slip values
                float slipRatioFront = Math.Max(Math.Abs(slipRatios[0]), Math.Abs(slipRatios[1]));
                float slipRatioRear = Math.Max(Math.Abs(slipRatios[2]), Math.Abs(slipRatios[3]));

                if (slipRatioFront > config.ThrottleSlip.FrontSlipThreshold || slipRatioRear > config.ThrottleSlip.RearSlipThreshold)
                {
                    float frontslipCoefecient = slipRatioFront * 3f;
                    frontslipCoefecient.ClipMax(5);
                    float rearSlipCoefecient = slipRatioRear * 5f;
                    rearSlipCoefecient.ClipMax(7.5f);

                    float magicValue = frontslipCoefecient + rearSlipCoefecient;
                    float percentage = magicValue * 1.0f / 12.5f;

                    if (percentage >= 0.05f)
                        p.AddAdaptiveTriggerToPacket(controllerIndex, Trigger.Right, TriggerMode.FEEDBACK, [1, (int)(config.ThrottleSlip.FeedbackStrength * percentage)]);

                    int freq = CalculateFrequency(percentage, config.ThrottleSlip.MinFrequency, config.ThrottleSlip.MaxFrequency, config.ThrottleSlip.InvertFrequency);
                    p.AddAdaptiveTriggerToPacket(controllerIndex, Trigger.Right, TriggerMode.VIBRATION, [0, config.ThrottleSlip.Amplitude, freq]);
                }
            }
        }

        if (p.Instructions == null) p.AddAdaptiveTriggerToPacket(0, Trigger.Right, TriggerMode.Normal, []);

        return p;
    }

    /// <summary>
    /// Calculates vibration frequency based on slip percentage with optional inversion.
    /// When inverted, high slip produces low frequency; when normal, high slip produces high frequency.
    /// </summary>
    /// <param name="percentage">Slip ratio percentage (0.0 to 1.0)</param>
    /// <param name="minFrequency">Minimum frequency value</param>
    /// <param name="maxFrequency">Maximum frequency value</param>
    /// <param name="invert">If true, high slip maps to low frequency; if false, high slip maps to high frequency</param>
    /// <returns>Calculated frequency value clamped between minFrequency and maxFrequency</returns>
    private static int CalculateFrequency(float percentage, int minFrequency, int maxFrequency, bool invert)
    {
        int freq;
        if (invert)
            freq = (int)(maxFrequency - (maxFrequency - minFrequency) * percentage);
        else
            freq = (int)(minFrequency + (maxFrequency - minFrequency) * percentage);

        freq.ClipMin(minFrequency);
        freq.ClipMax(maxFrequency);
        return freq;
    }
}
