using System.Globalization;
using System.Numerics;

namespace TechnicsSim.Mechanics.Solver;

/// <summary>
/// An exact signed angular-velocity ratio.
///
/// Gear trains multiply several tooth-count fractions. Keeping those fractions reduced as
/// integers means a closed loop can be tested for equality without a floating-point tolerance,
/// and only the final display/animation angle loses precision.
/// </summary>
public readonly struct ExactRatio : IEquatable<ExactRatio>
{
    public ExactRatio(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException("An exact ratio cannot have a zero denominator.");
        }

        if (numerator.IsZero)
        {
            Numerator = BigInteger.Zero;
            Denominator = BigInteger.One;
            return;
        }

        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public BigInteger Numerator { get; }

    public BigInteger Denominator { get; }

    public static ExactRatio Zero { get; } = new(0, 1);

    public static ExactRatio One { get; } = new(1, 1);

    public bool IsZero => Numerator.IsZero;

    public double ToDouble() => (double)Numerator / (double)Denominator;

    public ExactRatio Reciprocal() => IsZero
        ? throw new DivideByZeroException("Zero angular velocity has no reciprocal ratio.")
        : new ExactRatio(Denominator, Numerator);

    public static ExactRatio operator *(ExactRatio left, ExactRatio right) =>
        new(left.Numerator * right.Numerator, left.Denominator * right.Denominator);

    public static ExactRatio operator /(ExactRatio left, ExactRatio right) =>
        left * right.Reciprocal();

    public static ExactRatio operator -(ExactRatio value) =>
        new(-value.Numerator, value.Denominator);

    public bool Equals(ExactRatio other) =>
        Numerator == other.Numerator && Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is ExactRatio other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public static bool operator ==(ExactRatio left, ExactRatio right) => left.Equals(right);

    public static bool operator !=(ExactRatio left, ExactRatio right) => !left.Equals(right);

    /// <summary>Compact signed ratio notation used by both the CLI and UI.</summary>
    public override string ToString() => Denominator.IsOne
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Numerator}:{Denominator}");
}
