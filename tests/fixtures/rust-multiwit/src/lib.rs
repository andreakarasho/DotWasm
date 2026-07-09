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

use bindings::exports::test::multi::math::{Guest, Vec2};
struct C;
impl Guest for C {
    fn add(a: Vec2, b: Vec2) -> Vec2 { Vec2 { x: a.x+b.x, y: a.y+b.y } }
    fn len2(v: Vec2) -> i32 { v.x*v.x + v.y*v.y }
}
bindings::export!(C with_types_in bindings);
