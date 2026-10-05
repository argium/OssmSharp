import { initialize } from "./_content/OssmSharp.Web.Common/connection-ui.js";
import { initializeTheme } from "./_content/OssmSharp.Web.Common/theme.js";

initializeTheme();
const connection = initialize();
try {
  await Blazor.start({ circuit: connection.circuit });
} catch {
  connection.failed();
}
