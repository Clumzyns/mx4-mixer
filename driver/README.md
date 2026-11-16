# Custom virtual audio driver

Deferred. Mixer uses SteelSeries Sonar's signed virtual endpoints
(`SteelSeries Sonar - Gaming`, `Chat`, `Media`, `Aux`) and mixes them
to the master playback device.

A test-signed driver that exposes `Mixer - Game`, `Mixer - Chat`,
`Mixer - Media`, and `Mixer - Aux` can plug into the same backend later.
`VirtualBackends` already maps those names. Do not install a kernel
driver until that work is resumed.
