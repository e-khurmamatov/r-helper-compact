# Keyboard effects

Compact independently encodes standard matrix reports for the existing
`standard_matrix_ff` registry capability (currently Blade 14, 2022). This does
not enable any additional laptop models. No source code from OpenRazer or
open-razerkit is incorporated.

Protocol facts were cross-checked against OpenRazer's
[effect identifiers](https://github.com/openrazer/openrazer/blob/master/driver/razercommon.h),
[report fields](https://github.com/openrazer/openrazer/blob/master/driver/razerchromacommon.c)
and [model-specific dispatch](https://github.com/openrazer/openrazer/blob/master/driver/razerkbd_driver.c).

Reports are 90 bytes with transaction FF, command 03/0A, zero padding and XOR
of bytes 2 through 87 at byte 88. Additional effects:

| Effect | Declared data size | Argument bytes |
| --- | --- | --- |
| Reactive | 5 | 02, duration 1–4, R, G, B |
| Two-color breathing | 8 | 03, 02, R1, G1, B1, R2, G2, B2 |
| Starlight | 1 | 19, 01, 01, 00, FF, 00, 00, 00, 00 |

Starlight deliberately preserves the one-byte declared size and fixed green
parameters used for Blade 14 (2022). Other models' configurable Starlight
variants must not be inferred from this command. Reactive duration is a device
code, not a time in seconds.

Effects apply only on the explicit Apply action; AC/battery profiles retain
their existing brightness/logo behavior. OpenRGB ownership and device support
are checked by the controller before sending. Packet and synthetic UI tests
do not establish hardware compatibility; the new effects still need a manual
check on the target laptop.
