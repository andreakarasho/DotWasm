#![no_std]
extern crate alloc;
#[allow(warnings)]
mod bindings;
use alloc::string::String;
use core::alloc::{GlobalAlloc, Layout};
const HS: usize = 1<<20;
static mut HEAP: [u8; HS] = [0; HS];
static mut OFF: usize = 0;
struct B;
unsafe impl GlobalAlloc for B {
    unsafe fn alloc(&self, l: Layout) -> *mut u8 {
        let base = core::ptr::addr_of_mut!(HEAP) as *mut u8;
        let o = (OFF + l.align()-1) & !(l.align()-1);
        OFF = o + l.size(); base.add(o)
    }
    unsafe fn dealloc(&self, _: *mut u8, _: Layout) {}
}
#[global_allocator] static A: B = B;
#[panic_handler] fn p(_:&core::panic::PanicInfo)->! { core::arch::wasm32::unreachable() }

use bindings::test::imp::host_api;
use bindings::test::imp::host_api::Kv;
use bindings::Guest;

struct C;
impl Guest for C {
    // run() calls imported host-api funcs
    fn run() -> i32 {
        let s = host_api::add(40, 2);
        let r = host_api::record_sum(&Kv { key: String::from("k"), value: 100 });
        s + r
    }
    fn run_str() -> String {
        host_api::greet("world")
    }
}
bindings::export!(C with_types_in bindings);
