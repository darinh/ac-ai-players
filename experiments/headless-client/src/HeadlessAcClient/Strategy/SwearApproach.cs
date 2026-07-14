// SPDX-License-Identifier: AGPL-3.0-or-later

namespace HeadlessAcClient.Strategy;

internal static class SwearApproach
{
    internal static bool RequiresWalk(float? distanceSquared, float rangeUnits)
        => distanceSquared is float d2 && d2 > rangeUnits * rangeUnits;

    internal static bool IsConfirmedInRange(float? distanceSquared, float rangeUnits)
        => distanceSquared is float d2 && d2 <= rangeUnits * rangeUnits;
}
