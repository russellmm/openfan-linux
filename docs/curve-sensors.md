# Choosing sensors for curves

Open **Sensors** and check **Use in curves** beside the temperature sensors you want to
use. All checkboxes default to unchecked, including when opening an older profile.
The temperature-source dropdowns on **Home → Curves** and in **Edit graph** list only
checked sensors. Changes take effect immediately and save to the active profile; there
is no need to restart. Save configuration as… carries these choices into the new profile.

Every sensor row has a checkbox. Non-temperature readings have a disabled checkbox:
the graph editor maps °C to fan %, so RPM, voltage, power, counters and status flags
cannot be used as temperature inputs. Temperature alarm-limit readbacks remain separate
from measured temperatures and are not offered as graph sources.

Unchecking a source removes it from the menus without changing curves that already use
it. Such a curve keeps its sensor assignment, live reading and output; its closed selector
shows the source name with **(not listed)**. Enable the checkbox again to put that source
back into the list. A disconnected current source is shown as **(unavailable)**, and its
saved identity is retained. A new curve with no checked sources says **Choose sources on
Sensors**. Sensor aliases are used in both curve selectors.

Sensor-group visibility is independent of this selection. Hiding or collapsing a group
does not uncheck its sources. A curve's required inputs continue sampling while active;
listed choices also keep sampling while Home or a graph editor is open, even when their
table group is hidden. Unchecked sources already assigned to a curve remain available to
that curve. Unchecking a source never changes fan duty, calibration or firmware thresholds.

## Shared readings

The Sensors table, curve cards and graph editor use the same merged hardware inventory
and the same published readings from FanApp. The table computes Current/Min/Max/Average
from those samples; curve selectors read the current sample by the same sensor identity.
The existing control-loop validity checks remain in place. No second hardware reader or
estimated sensor values are introduced by the checkbox feature.

CurveSensorSelection filters the shared inventory using the profile's `curveSensorIds`
list. Both curve selectors consume FanApp.CurveSensors, and change notifications refresh
open editors and cards. Checked sensor identities are retained across disconnection and
profile save/load. An empty or absent list means no menu entries; existing curve
`sensorId` assignments are preserved independently.
