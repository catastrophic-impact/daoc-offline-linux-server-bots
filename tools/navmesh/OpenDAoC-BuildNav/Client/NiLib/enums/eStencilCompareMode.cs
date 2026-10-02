namespace MNL {
  /* This enum contains the options for doing stencil buffer tests. */
  public enum eStencilCompareMode : uint {
    TEST_NEVER = 0, /* Test will allways return false. Nothing is drawn at all. */
    TEST_LESS = 1, /* The test will only succeed if the pixel is nearer than the previous pixel. */
    TEST_EQUAL = 2, /* Test will only succeed if the z value of the pixel to be drawn is equal to the value of the previous drawn pixel. */
    TEST_LESS_EQUAL = 3, /* Test will succeed if the z value of the pixel to be drawn is smaller than or equal to the value in the Stencil Buffer. */
    TEST_GREATER = 4, /* Opposite of TEST_LESS. */
    TEST_NOT_EQUAL = 5, /* Test will succeed if the z value of the pixel to be drawn is NOT equal to the value of the previously drawn pixel. */
    TEST_GREATER_EQUAL = 6, /* Opposite of TEST_LESS_EQUAL. */
    TEST_ALWAYS = 7, /* Test will allways succeed. The Stencil Buffer value is ignored. */
  }
}
