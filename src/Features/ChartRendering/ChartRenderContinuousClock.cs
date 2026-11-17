namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Maps one real clock source (seconds, ticks, any unit) onto the render timeline.
    //
    // While a render is active the mapped value advances by exactly the video time that was
    // rendered, however long encoding takes. When the render ends the source keeps running at real
    // speed from where the video stopped, so timestamps a key viewer stored during the render stay
    // valid afterwards. Jumping straight back to the real clock would move "now" backwards (or far
    // forwards) and break rain lengths and KPS windows, which is what happened with JRP before.
    //
    // Not thread-safe on its own; callers hold their own lock.
    internal sealed class ChartRenderContinuousClock
    {
        private double offset;
        private bool begun;
        private double mappedAtBegin;
        private double videoAtBegin;

        // real: the source's current reading. video: rendered video time, in the same unit.
        public double Map(double real, bool rendering, double video)
        {
            if (!rendering)
            {
                return real + offset;
            }

            if (!begun)
            {
                // The first read of a render anchors the source at its current mapped value.
                mappedAtBegin = real + offset;
                videoAtBegin = video;
                begun = true;
            }

            return mappedAtBegin + video - videoAtBegin;
        }

        // Called once when the render ends, with the source's current real reading.
        public void EndRender(double real, double video)
        {
            if (begun)
            {
                offset = mappedAtBegin + video - videoAtBegin - real;
            }

            begun = false;
        }

        public void Reset()
        {
            offset = 0.0;
            begun = false;
            mappedAtBegin = 0.0;
            videoAtBegin = 0.0;
        }
    }
}
