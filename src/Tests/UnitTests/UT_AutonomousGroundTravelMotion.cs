using System;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    public class UT_AutonomousGroundTravelMotion
    {
        [TestCase(191)]
        [TestCase(252)]
        [TestCase(298)]
        [TestCase(390)]
        [TestCase(50)] // An actual snare/health-limited speed is never raised.
        public void FlatGroundAndSmallHeightNoiseKeepExactRequestedSpeed(short speed)
        {
            for (int angle = 0; angle < 360; angle++)
            foreach (float height in new[] { -0.1f, 0, 0.1f, 4f })
            {
                double radians = angle * Math.PI / 180;
                Vector3 delta = new((float)Math.Cos(radians) * 30, (float)Math.Sin(radians) * 30, height);
                Assert.That(AutonomousGroundTravelMotion.TryCalculate(delta, speed, out var m), Is.True);
                Assert.That(m.HorizontalSpeed, Is.EqualTo(speed));
                Assert.That((ushort)m.HorizontalSpeed, Is.EqualTo(speed));
                Assert.That(new Vector2(m.Velocity.X,m.Velocity.Y).Length(), Is.EqualTo((double)speed).Within(0.001));
            }
        }

        [TestCase(-222, 14.66f, 462.6f)] // Cediswell: was 82.75 at requested 191.
        [TestCase(-67.19f, 227.58f, -299.44f)] // Harerisren: was 118.61.
        [TestCase(12, 0, 24)] // A short stair/height step, not a long hill.
        [TestCase(200, 200, 0)]
        public void RecordedTravelSegmentsRunAndArriveWithoutOldTimerPause(float x,float y,float z)
        {
            Vector3 delta = new(x,y,z);
            Assert.That(AutonomousGroundTravelMotion.TryCalculate(delta,191,out var m),Is.True);
            Assert.That(m.HorizontalSpeed,Is.EqualTo(191));
            double duration = new Vector2(x,y).Length()/191d;
            Assert.That(m.ArrivalMilliseconds,Is.EqualTo(Math.Ceiling(duration*1000)).Within(1));
            Assert.That(Vector3.Distance(m.Velocity*(float)duration,delta),Is.LessThan(0.001));
            Assert.That(Math.Abs(m.Velocity.Z),Is.LessThanOrEqualTo(508));
        }

        [TestCase(0,0,14)]
        [TestCase(0.001f,0,10000)]
        [TestCase(0.9f,-1.56f,-13.89f)] // Near-vertical Osoiswell correction: bounded, no packet overflow.
        [TestCase(1,1,-100000)]
        public void VerticalCorrectionsAreFiniteBoundedAndKeepTheirOriginalEndpoint(float x,float y,float z)
        {
            Vector3 delta=new(x,y,z);
            Assert.That(AutonomousGroundTravelMotion.TryCalculate(delta,191,out var m),Is.True);
            Assert.That(float.IsFinite(m.Velocity.Z),Is.True);
            Assert.That(Math.Abs(m.Velocity.Z),Is.LessThanOrEqualTo(508));
            Assert.That(m.HorizontalSpeed,Is.InRange(0,191));
            Assert.That(m.ArrivalMilliseconds,Is.GreaterThan(0));
            float exactTime=delta.Length()/m.Velocity.Length();
            Assert.That(Vector3.Distance(m.Velocity*exactTime,delta),Is.LessThan(0.02));
        }

        [Test]
        public void InvalidOrStoppedMovementIsNotInvented()
        {
            Assert.That(AutonomousGroundTravelMotion.TryCalculate(Vector3.One,0,out _),Is.False);
            Assert.That(AutonomousGroundTravelMotion.TryCalculate(Vector3.Zero,191,out _),Is.False);
            Assert.That(AutonomousGroundTravelMotion.TryCalculate(new(float.NaN,1,2),191,out _),Is.False);
            Assert.That(AutonomousGroundTravelMotion.TryCalculate(new(1,1,float.PositiveInfinity),191,out _),Is.False);
        }
    }
}
