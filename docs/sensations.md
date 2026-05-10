# Default sensation library

The default OWO Skin library ships with six sensations, ordered by
perceived intensity:

| # | Sensation              | Use                              | Zones          | Frequency    | Intensity |
|---|------------------------|----------------------------------|----------------|--------------|-----------|
| 1 | `deploy_success`       | Production deploy succeeded      | upper back     | 25 Hz (warm) | 45%       |
| 2 | `chat_tap`             | Casual @-mention                 | left arm       | 60 Hz        | 50%       |
| 3 | `compile_error_mild`   | Single warning                   | left pectoral  | 55 Hz        | 65%       |
| 4 | `chat_zap`             | High-priority mention / DM       | left arm       | 95 Hz (sharp)| 70%       |
| 5 | `test_failed`          | Test went red                    | abdomen (both) | 75 Hz        | 75%       |
| 6 | `compile_error_severe` | Multiple errors / broken build   | both pectorals | 100 Hz (sharp)| 90%      |

## Zone conventions

- **Chest (`pectoral_l`, `pectoral_r`):** build events. Pec = "the
  thing you're building."
- **Arm (`arm_l`):** chat / external attention.
- **Abdomen (`abdominal_l`, `abdominal_r`):** test events.
- **Upper back (`dorsal_l`, `dorsal_r`):** ambient status / passive
  good news.

These conventions help users decode events without consciously
processing the sensation; over time you learn "left arm = Slack",
"chest = build", etc.

## Frequency vs intensity

The OWO Skin's perceived sharpness is driven primarily by
**frequency**, not intensity. High frequency (80–100 Hz) feels
electric and sharp; low frequency (20–40 Hz) feels warm and
diffuse. Use frequency to differentiate severity tiers and
intensity for fine-grained tuning within a tier.

Intensity below ~45% is barely perceptible on most calibration
profiles, so 45 is treated as the practical floor.
