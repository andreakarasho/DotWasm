#![no_std]
extern crate alloc;
#[allow(warnings)]
mod bindings;
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

use bindings::test::hold::store::{make, Node};

static mut HELD: Option<Node> = None;

struct C;
impl bindings::Guest for C {
    fn create() { unsafe { HELD = Some(make(42)); } }      // hold across calls, do NOT drop
    fn reuse() -> i32 {
        unsafe { HELD.as_ref().map(|n| n.label()).unwrap_or(-1) }
    }
    fn release() { unsafe { HELD = None; } }               // drop the held node (resource.drop)
}
bindings::export!(C with_types_in bindings);
