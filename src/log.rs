//! Log helpers so every line carries the `[ColonistAutonomy]` tag.

use rimworld_rs::prelude::*;

use crate::settings::Settings;

pub fn info(ctx: &Context, message: String) {
    ctx.log(message);
}

pub fn debug(ctx: &Context, settings: &Settings, message: String) {
    if settings.debug {
        ctx.log(format!("[ColonistAutonomy] DEBUG {message}"));
    }
}

pub fn warn(ctx: &Context, message: String) {
    ctx.log(format!("[ColonistAutonomy] WARN {message}"));
}
