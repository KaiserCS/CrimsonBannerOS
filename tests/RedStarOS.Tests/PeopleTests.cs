// SPDX-License-Identifier: GPL-3.0-or-later
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>The name the shell shows for a person (the login screen's own tests are crimson-login's, greeter/test).</summary>
public class PeopleTests
{
    [Fact]
    public void Shown_name_comes_from_the_passwd_comment()
    {
        const string passwd = "root:x:0:0:root:/root:/bin/bash\nkaiser:x:1000:1000:Steven Kaiser,,,:/home/kaiser:/bin/bash\nbare:x:1001:1001::/home/bare:/bin/bash\n";
        Assert.Equal("Steven Kaiser", People.ShownName("kaiser", passwd));
        Assert.Equal("bare", People.ShownName("bare", passwd));
        Assert.Equal("nobody-here", People.ShownName("nobody-here", passwd));
    }
}
