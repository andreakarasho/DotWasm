#![no_std]
extern crate alloc;

#[allow(warnings)]
mod bindings;

use alloc::format;
use alloc::string::{String, ToString};
use alloc::vec::Vec;
use core::cell::Cell;
use bindings::exports::test::comp::ops::{Guest, GuestCounter, Point, Shape, Color, Perms};

// Minimal bump allocator so the component needs no WASI imports.
use core::alloc::{GlobalAlloc, Layout};
const HEAP_SIZE: usize = 4 << 20;
static mut HEAP: [u8; HEAP_SIZE] = [0; HEAP_SIZE];
static mut OFFSET: usize = 0;
struct Bump;
unsafe impl GlobalAlloc for Bump {
    unsafe fn alloc(&self, l: Layout) -> *mut u8 {
        let base = core::ptr::addr_of_mut!(HEAP) as *mut u8;
        let off = (OFFSET + l.align() - 1) & !(l.align() - 1);
        OFFSET = off + l.size();
        base.add(off)
    }
    unsafe fn dealloc(&self, _: *mut u8, _: Layout) {}
}
#[global_allocator]
static ALLOC: Bump = Bump;

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! { core::arch::wasm32::unreachable() }

struct Component;

impl Guest for Component {
    type Counter = MyCounter;

    fn add_point(a: Point, b: Point) -> Point { Point { x: a.x + b.x, y: a.y + b.y } }
    fn sum_list(xs: Vec<i32>) -> i32 { xs.iter().sum() }
    fn make_list(n: u32) -> Vec<u32> { (0..n).collect() }
    fn describe(s: Shape) -> String {
        match s {
            Shape::Circle(r) => format!("circle {r}"),
            Shape::Rect(p) => format!("rect {} {}", p.x, p.y),
            Shape::Unit => "unit".to_string(),
        }
    }
    fn divide(a: i32, b: i32) -> Result<i32, String> {
        if b == 0 { Err("div by zero".to_string()) } else { Ok(a / b) }
    }
    fn maybe_inc(x: Option<i32>) -> Option<i32> { x.map(|v| v + 1) }
    fn next_color(c: Color) -> Color {
        match c { Color::Red => Color::Green, Color::Green => Color::Blue, Color::Blue => Color::Red }
    }
    fn perm_bits(p: Perms) -> u32 { p.bits() as u32 }
    fn swap(t: (i32, f64)) -> (f64, i32) { (t.1, t.0) }
    #[allow(clippy::too_many_arguments)]
    fn big_sum(a:i32,b:i32,c:i32,d:i32,e:i32,f:i32,g:i32,h:i32,i:i32,j:i32,k:i32,l:i32,m:i32,n:i32,o:i32,p:i32,q:i32)->i32 {
        a+b+c+d+e+f+g+h+i+j+k+l+m+n+o+p+q
    }
    fn f32_add(a:f32,b:f32)->f32 { a+b }
    fn u64_add(a:u64,b:u64)->u64 { a+b }
}

struct MyCounter { value: Cell<i32> }
impl GuestCounter for MyCounter {
    fn new(init: i32) -> Self { MyCounter { value: Cell::new(init) } }
    fn increment(&self) -> i32 { let v = self.value.get() + 1; self.value.set(v); v }
    fn get(&self) -> i32 { self.value.get() }
}

bindings::export!(Component with_types_in bindings);
