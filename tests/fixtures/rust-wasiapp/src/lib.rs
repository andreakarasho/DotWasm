#[allow(warnings)]
mod bindings;
use std::time::{SystemTime, UNIX_EPOCH};

struct C;
impl bindings::Guest for C {
    fn run() -> u64 {
        println!("hello from wasi stdout");
        eprintln!("hello from wasi stderr");
        let now = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs();
        now
    }
}
bindings::export!(C with_types_in bindings);
