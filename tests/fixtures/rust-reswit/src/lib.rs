#![no_std]
extern crate alloc;
#[allow(warnings)]
mod bindings;
use core::alloc::{GlobalAlloc, Layout};
use core::cell::Cell;
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

use bindings::exports::test::res::entity::{Guest as EntGuest, GuestNode};
use bindings::exports::test::res::graph::{Guest as GraphGuest, NodeBorrow};

struct Node { v: Cell<i32> }
impl GuestNode for Node {
    fn new(v: i32) -> Self { Node { v: Cell::new(v) } }
    fn value(&self) -> i32 { self.v.get() }
}

struct C;
impl EntGuest for C { type Node = Node; }
impl GraphGuest for C {
    fn combine(a: NodeBorrow, b: NodeBorrow) -> i32 {
        a.get::<Node>().value() + b.get::<Node>().value()
    }
}
bindings::export!(C with_types_in bindings);
